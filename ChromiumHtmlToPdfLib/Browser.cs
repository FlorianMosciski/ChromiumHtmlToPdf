﻿//
// Communicator.cs
//
// Author: Kees van Spelde <sicos2002@hotmail.com>
//
// Copyright (c) 2017-2023 Magic-Sessions. (www.magic-sessions.com)
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NON INFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
//

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ChromiumHtmlToPdfLib.Exceptions;
using ChromiumHtmlToPdfLib.Helpers;
using ChromiumHtmlToPdfLib.Protocol;
using ChromiumHtmlToPdfLib.Protocol.Network;
using ChromiumHtmlToPdfLib.Protocol.Page;
using ChromiumHtmlToPdfLib.Settings;
using Microsoft.Extensions.Logging;
using Base = ChromiumHtmlToPdfLib.Protocol.Network.Base;
// ReSharper disable UnusedMember.Global
// ReSharper disable AccessToDisposedClosure
// ReSharper disable AccessToModifiedClosure

namespace ChromiumHtmlToPdfLib;

/// <summary>
///     Handles all the communication tasks with Chromium remote dev tools
/// </summary>
/// <remarks>
///     See https://chromedevtools.github.io/devtools-protocol/
/// </remarks>
#if (NETSTANDARD2_0)
public class Browser : IDisposable
#else
public class Browser : IDisposable, IAsyncDisposable
#endif
{
    #region Fields
    /// <summary>
    ///     Used to make the logging thread safe
    /// </summary>
    private readonly object _lock = new();

    /// <summary>
    ///     When set then logging is written to this ILogger instance
    /// </summary>
    private readonly ILogger _logger;

    /// <summary>
    ///     A connection to the browser (Chrome or Edge)
    /// </summary>
    private Connection _browserConnection;

    /// <summary>
    ///     A connection to a page
    /// </summary>
    private Connection _pageConnection;

    private string _instanceId;

    /// <summary>
    ///     Keeps track is we already disposed our resources
    /// </summary>
    private bool _disposed;
    #endregion

    #region Properties
    /// <summary>
    ///     An unique id that can be used to identify the logging of the converter when
    ///     calling the code from multiple threads and writing all the logging to the same file
    /// </summary>
    public string InstanceId
    {
        get => _instanceId;
        set
        {
            _instanceId = value;
            _browserConnection.InstanceId = value;
            _pageConnection.InstanceId = value;
        }
    }
    #endregion

    #region Constructor
    /// <summary>
    ///     Makes this object and sets the Chromium remote debugging url
    /// </summary>
    /// <param name="browser">The websocket to the browser</param>
    /// <param name="logger">
    ///     When set then logging is written to this ILogger instance for all conversions at the Information
    ///     log level
    /// </param>
    /// <param name="timeout">Websocket open timeout in milliseconds</param>
    internal Browser(Uri browser, ILogger logger, int timeout)
    {
        _logger = logger;

        // Open a websocket to the browser
        _browserConnection = new Connection(browser.ToString(), logger, timeout);
        _browserConnection.OnError += OnOnError;

        var message = new Message { Method = "Target.createTarget" };
        message.Parameters.Add("url", "about:blank");

        var result = _browserConnection.SendForResponseAsync(message, CancellationToken.None).ConfigureAwait(false).GetAwaiter().GetResult();
        var page = Page.FromJson(result);
        var pageUrl = $"{browser.Scheme}://{browser.Host}:{browser.Port}/devtools/page/{page.Result.TargetId}";

        // Open a websocket to the page
        _pageConnection = new Connection(pageUrl, logger, timeout);
        _pageConnection.OnError += OnOnError;
    }
    #endregion

    #region OnError
    private void OnOnError(object sender, string error)
    {
        WriteToLog($"An error occurred: '{error}'");
    }
    #endregion

    #region NavigateToAsync
    /// <summary>
    ///     Instructs Chromium to navigate to the given <paramref name="uri" />
    /// </summary>
    /// <param name="safeUrls">A list with URL's that are safe to load</param>
    /// <param name="useCache">When <c>true</c> then caching will be enabled</param>
    /// <param name="uri"></param>
    /// <param name="html"></param>
    /// <param name="countdownTimer">
    ///     If a <see cref="CountdownTimer" /> is set then
    ///     the method will raise an <see cref="ConversionTimedOutException" /> if the
    ///     <see cref="CountdownTimer" /> reaches zero before finishing navigation
    /// </param>
    /// <param name="mediaLoadTimeout">
    ///     When set a timeout will be started after the DomContentLoaded
    ///     event has fired. After a timeout the NavigateTo method will exit as if the page
    ///     has been completely loaded
    /// </param>
    /// <param name="urlBlacklist">A list with URL's that need to be blocked (use * as a wildcard)</param>
    /// <param name="logNetworkTraffic">When enabled network traffic is also logged</param>
    /// <param name="cancellationToken"><see cref="CancellationToken"/></param>
    /// <exception cref="ChromiumException">Raised when an error is returned by Chromium</exception>
    /// <exception cref="ConversionTimedOutException">Raised when <paramref name="countdownTimer" /> reaches zero</exception>
    internal async Task NavigateToAsync(
        List<string> safeUrls,
        bool useCache,
        ConvertUri uri = null,
        string html = null,
        CountdownTimer countdownTimer = null,
        int? mediaLoadTimeout = null,
        List<string> urlBlacklist = null,
        bool logNetworkTraffic = false,
        CancellationToken cancellationToken = default)
    {
        var waitEvent = new ManualResetEvent(false);
        var mediaLoadTimeoutCancellationTokenSource = new CancellationTokenSource();
        var navigationError = string.Empty;
        var waitForNetworkIdle = false;
        var mediaTimeoutTaskSet = false;
        var absoluteUri = uri?.AbsoluteUri.Substring(0, uri.AbsoluteUri.LastIndexOf('/') + 1);

        #region Message handler
        // ReSharper disable once AsyncVoidLambda
        var messageHandler = new EventHandler<string>(async delegate(object _, string data)
        {
            //System.IO.File.AppendAllText("d:\\logs.txt", $"{DateTime.Now:yyyy-MM-ddTHH:mm:ss.fff} - {data}{Environment.NewLine}");
            var message = Base.FromJson(data);

            switch (message.Method)
            {
                case "Network.requestWillBeSent":
                    var requestWillBeSent = RequestWillBeSent.FromJson(data);
                    WriteToLog($"Request sent with request id '{requestWillBeSent.Params.RequestId}' " +
                               $"for url '{requestWillBeSent.Params.Request.Url}' " +
                               $"with method '{requestWillBeSent.Params.Request.Method}' " +
                               $"and type '{requestWillBeSent.Params.Type}'");
                    break;

                case "Network.dataReceived":
                    var dataReceived = DataReceived.FromJson(data);
                    WriteToLog($"Data received for request id '{dataReceived.Params.RequestId}' " +
                               $"with length '{dataReceived.Params.DataLength}'");
                    break;

                case "Network.responseReceived":
                    var responseReceived = ResponseReceived.FromJson(data);
                    var response = responseReceived.Params.Response;

                    var logMessage =
                        $"{(response.FromDiskCache ? "Cached response" : "Response")} received for request id '{responseReceived.Params.RequestId}' and url '{response.Url}'";

                    if (!string.IsNullOrWhiteSpace(response.RemoteIpAddress))
                        logMessage +=
                            $" from ip '{response.RemoteIpAddress}' on port '{response.RemotePort}' with status '{response.Status}{(!string.IsNullOrWhiteSpace(response.StatusText) ? $" ({response.StatusText})" : string.Empty)}'";
                    WriteToLog(logMessage);
                    break;

                case "Network.loadingFinished":
                    var loadingFinished = LoadingFinished.FromJson(data);
                    WriteToLog($"Loading finished for request id '{loadingFinished.Params.RequestId}' " +
                               $"{(loadingFinished.Params.EncodedDataLength > 0 ? $"with encoded data length '{loadingFinished.Params.EncodedDataLength}'" : string.Empty)}");
                    break;

                case "Network.loadingFailed":
                    var loadingFailed = LoadingFailed.FromJson(data);
                    WriteToLog($"Loading failed for request id '{loadingFailed.Params.RequestId}' " +
                               $"and type '{loadingFailed.Params.Type}' " +
                               $"with error '{loadingFailed.Params.ErrorText}'");
                    break;

                case "Network.requestServedFromCache":
                    var requestServedFromCache = RequestServedFromCache.FromJson(data);
                    WriteToLog($"The request with id '{requestServedFromCache.Params.RequestId}' is served from cache");
                    break;

                case "Fetch.requestPaused":
                {
                    var fetch = Fetch.FromJson(data);
                    var requestId = fetch.Params.RequestId;
                    var url = fetch.Params.Request.Url;
                    var isSafeUrl = safeUrls.Contains(url);
                    var isAbsoluteFileUri = absoluteUri != null &&
                                            url.StartsWith("file://", StringComparison.InvariantCultureIgnoreCase) &&
                                            url.StartsWith(absoluteUri, StringComparison.InvariantCultureIgnoreCase);

                    if (!RegularExpression.IsRegExMatch(urlBlacklist, url, out var matchedPattern) ||
                        isAbsoluteFileUri || isSafeUrl)
                    {
                        if (isSafeUrl)
                            WriteToLog($"The url '{url}' has been allowed because it is on the safe url list");
                        else if (isAbsoluteFileUri)
                            WriteToLog($"The file url '{url}' has been allowed because it start with the absolute uri '{absoluteUri}'");
                        else
                            WriteToLog($"The url '{url}' has been allowed because it did not match anything on the url blacklist");

                        var fetchContinue = new Message { Method = "Fetch.continueRequest" };
                        fetchContinue.Parameters.Add("requestId", requestId);
                        await _pageConnection.SendAsync(fetchContinue, cancellationToken);
                    }
                    else
                    {
                        WriteToLog($"The url '{url}' has been blocked by url blacklist pattern '{matchedPattern}'");

                        var fetchFail = new Message { Method = "Fetch.failRequest" };
                        fetchFail.Parameters.Add("requestId", requestId);

                        // Failed, Aborted, TimedOut, AccessDenied, ConnectionClosed, ConnectionReset, ConnectionRefused,
                        // ConnectionAborted, ConnectionFailed, NameNotResolved, InternetDisconnected, AddressUnreachable,
                        // BlockedByClient, BlockedByResponse
                        fetchFail.Parameters.Add("errorReason", "BlockedByClient");
                        await _pageConnection.SendAsync(fetchFail, cancellationToken);
                    }

                    break;
                }

                default:
                {
                    var page = Protocol.Page.Event.FromJson(data);

                    switch (page.Method)
                    {
                        // The DOMContentLoaded event is fired when the document has been completely loaded and parsed, without
                        // waiting for stylesheets, images, and sub frames to finish loading (the load event can be used to
                        // detect a fully-loaded page).
                        case "Page.lifecycleEvent" when page.Params?.Name == "DOMContentLoaded":

                            WriteToLog(
                                "The 'Page.lifecycleEvent' with param name 'DomContentLoaded' has been fired, the dom content is now loaded and parsed, waiting for stylesheets, images and sub frames to finish loading");

                            if (mediaLoadTimeout.HasValue && !mediaTimeoutTaskSet)
                                try
                                {
                                    WriteToLog($"Media load timeout has a value of {mediaLoadTimeout.Value} milliseconds, setting media load timeout task");

                                    await Task.Run(async delegate
                                    {
                                        await Task.Delay(mediaLoadTimeout.Value, mediaLoadTimeoutCancellationTokenSource.Token);
                                        WriteToLog($"Media load timeout task timed out after {mediaLoadTimeout.Value} milliseconds");
                                        waitEvent?.Set();
                                    }, mediaLoadTimeoutCancellationTokenSource.Token);

                                    mediaTimeoutTaskSet = true;
                                }
                                catch
                                {
                                    // Ignore
                                }

                            break;

                        case "Page.frameNavigated":
                            WriteToLog("The 'Page.frameNavigated' event has been fired, waiting for the 'Page.lifecycleEvent' with name 'networkIdle'");
                            waitForNetworkIdle = true;
                            break;

                        case "Page.lifecycleEvent" when page.Params?.Name == "networkIdle" && waitForNetworkIdle:
                            WriteToLog("The 'Page.lifecycleEvent' event with name 'networkIdle' has been fired, the page is now fully loaded");
                            waitEvent?.Set();
                            break;

                        default:
                            var pageNavigateResponse = NavigateResponse.FromJson(data);
                            if (!string.IsNullOrEmpty(pageNavigateResponse.Result?.ErrorText) &&
                                !pageNavigateResponse.Result.ErrorText.Contains("net::ERR_BLOCKED_BY_CLIENT"))
                            {
                                navigationError = $"{pageNavigateResponse.Result.ErrorText} occurred when navigating to the page '{uri}'";
                                waitEvent?.Set();
                            }

                            break;
                    }

                    break;
                }
            }
        });
        #endregion

        if (uri?.RequestHeaders?.Count > 0)
        {
            WriteToLog("Setting request headers");
            var networkMessage = new Message { Method = "Network.setExtraHTTPHeaders" };
            networkMessage.AddParameter("headers", uri.RequestHeaders);
            await _pageConnection.SendForResponseAsync(networkMessage, cancellationToken);
        }

        if (logNetworkTraffic)
        {
            WriteToLog("Enabling network traffic logging");
            var networkMessage = new Message { Method = "Network.enable" };
            await _pageConnection.SendForResponseAsync(networkMessage, cancellationToken);
        }

        WriteToLog(useCache ? "Enabling caching" : "Disabling caching");

        var cacheMessage = new Message { Method = "Network.setCacheDisabled" };
        cacheMessage.Parameters.Add("cacheDisabled", !useCache);
        await _pageConnection.SendForResponseAsync(cacheMessage, cancellationToken);

        // Enables issuing of requestPaused events. A request will be paused until client calls one of failRequest, fulfillRequest or continueRequest/continueWithAuth
        if (urlBlacklist?.Count > 0)
        {
            WriteToLog("Enabling Fetch to block url's that are in the url blacklist'");
            await _pageConnection.SendForResponseAsync(new Message { Method = "Fetch.enable" }, cancellationToken);
        }

        // Enables page domain notifications
        await _pageConnection.SendForResponseAsync(new Message { Method = "Page.enable" }, cancellationToken);

        var lifecycleEventEnabledMessage = new Message { Method = "Page.setLifecycleEventsEnabled" };
        lifecycleEventEnabledMessage.AddParameter("enabled", true);
        await _pageConnection.SendForResponseAsync(lifecycleEventEnabledMessage, cancellationToken);

        _pageConnection.MessageReceived += messageHandler;
        _pageConnection.Closed += (_, _) => waitEvent?.Set();

        if (uri != null)
        {
            // Navigates current page to the given URL
            var pageNavigateMessage = new Message { Method = "Page.navigate" };
            pageNavigateMessage.AddParameter("url", uri.ToString());
            await _pageConnection.SendAsync(pageNavigateMessage, cancellationToken);
        }
        else if (!string.IsNullOrWhiteSpace(html))
        {
            WriteToLog("Getting page frame tree");
            var pageGetFrameTree = new Message { Method = "Page.getFrameTree" };
            var frameTree = await _pageConnection.SendForResponseAsync(pageGetFrameTree, cancellationToken);
            var frameResult = FrameTree.FromJson(frameTree);

            WriteToLog("Setting document content");

            var pageSetDocumentContent = new Message { Method = "Page.setDocumentContent" };
            pageSetDocumentContent.AddParameter("frameId", frameResult.Result.FrameTree.Frame.Id);
            pageSetDocumentContent.AddParameter("html", html);
            await _pageConnection.SendForResponseAsync(pageSetDocumentContent, cancellationToken);
            // When using setDocumentContent a Page.frameNavigated event is never fired so we have to set the waitForNetworkIdle to true our self
            waitForNetworkIdle = true;

            WriteToLog("Document content set");
        }
        else
        {
            throw new ArgumentException("Uri and html are both null");
        }

        if (countdownTimer != null)
        {
            waitEvent.WaitOne(countdownTimer.MillisecondsLeft);
            if (countdownTimer.MillisecondsLeft == 0)
                throw new ConversionTimedOutException($"The {nameof(NavigateToAsync)} method timed out");
        }
        else
        {
            waitEvent.WaitOne();
        }

        if (mediaTimeoutTaskSet)
        {
            mediaLoadTimeoutCancellationTokenSource.Cancel();
            mediaLoadTimeoutCancellationTokenSource.Dispose();
        }

        var lifecycleEventDisabledMessage = new Message { Method = "Page.setLifecycleEventsEnabled" };
        lifecycleEventDisabledMessage.AddParameter("enabled", false);

        // Disables page domain notifications
        await _pageConnection.SendForResponseAsync(lifecycleEventDisabledMessage, cancellationToken);
        await _pageConnection.SendForResponseAsync(new Message { Method = "Page.disable" }, cancellationToken);

        // Disables the fetch domain
        if (urlBlacklist?.Count > 0)
        {
            WriteToLog("Disabling Fetch");
            await _pageConnection.SendForResponseAsync(new Message { Method = "Fetch.disable" }, cancellationToken);
        }

        if (logNetworkTraffic)
        {
            WriteToLog("Disabling network traffic logging");
            var networkMessage = new Message { Method = "Network.disable" };
            await _pageConnection.SendForResponseAsync(networkMessage, cancellationToken);
        }

        _pageConnection.MessageReceived -= messageHandler;

        waitEvent.Dispose();
        waitEvent = null;

        if (!string.IsNullOrEmpty(navigationError))
        {
            WriteToLog(navigationError);
            throw new ChromiumNavigationException(navigationError);
        }
    }
    #endregion

    #region WaitForWindowStatus
    /// <summary>
    ///     Waits until the javascript window.status is returning the given <paramref name="status" />
    /// </summary>
    /// <param name="status">The case insensitive status</param>
    /// <param name="timeout">Continue after reaching the set timeout in milliseconds</param>
    /// <returns><c>true</c> when window status matched, <c>false</c> when timing out</returns>
    /// <exception cref="ChromiumException">Raised when an error is returned by Chromium</exception>
    public bool WaitForWindowStatus(string status, int timeout = 60000)
    {
        return WaitForWindowStatusAsync(status, timeout).ConfigureAwait(false).GetAwaiter().GetResult();
    }
    #endregion

    #region WaitForWindowStatusAsync
    /// <summary>
    ///     Waits until the javascript window.status is returning the given <paramref name="status" />
    /// </summary>
    /// <param name="status">The case insensitive status</param>
    /// <param name="timeout">Continue after reaching the set timeout in milliseconds</param>
    /// <param name="cancellationToken"><see cref="CancellationToken"/></param>
    /// <returns><c>true</c> when window status matched, <c>false</c> when timing out</returns>
    /// <exception cref="ChromiumException">Raised when an error is returned by Chromium</exception>
    public async Task<bool> WaitForWindowStatusAsync(string status, int timeout = 60000, CancellationToken cancellationToken = default)
    {
        var message = new Message { Method = "Runtime.evaluate" };
        message.AddParameter("expression", "window.status;");
        message.AddParameter("silent", true);
        message.AddParameter("returnByValue", true);

        var waitEvent = new ManualResetEvent(false);
        var match = false;

        _pageConnection.MessageReceived += MessageReceived;

        var stopWatch = new Stopwatch();
        stopWatch.Start();

        while (!match)
        {
            await _pageConnection.SendAsync(message, cancellationToken);
            waitEvent.WaitOne(10);
            if (stopWatch.ElapsedMilliseconds >= timeout) break;
        }

        stopWatch.Stop();
        _pageConnection.MessageReceived -= MessageReceived;

        return match;

        void MessageReceived(object sender, string data)
        {
            var evaluate = Evaluate.FromJson(data);
            if (evaluate.Result?.Result?.Value != status) return;
            match = true;
            waitEvent.Set();
        }
    }
    #endregion

    #region RunJavascript
    /// <summary>
    ///     Runs the given javascript after the page has been fully loaded
    /// </summary>
    /// <param name="script">The javascript to run</param>
    /// <exception cref="ChromiumException">Raised when an error is returned by Chromium</exception>
    public void RunJavascript(string script)
    {
        RunJavascriptAsync(script).ConfigureAwait(false).GetAwaiter().GetResult();
    }
    #endregion

    #region RunJavascriptAsync
    /// <summary>
    ///     Runs the given javascript after the page has been fully loaded
    /// </summary>
    /// <param name="script">The javascript to run</param>
    /// <param name="cancellationToken"><see cref="CancellationToken"/></param>
    /// <exception cref="ChromiumException">Raised when an error is returned by Chromium</exception>
    public async Task RunJavascriptAsync(string script, CancellationToken cancellationToken = default)
    {
        var message = new Message { Method = "Runtime.evaluate" };
        message.AddParameter("expression", script);
        message.AddParameter("silent", false);
        message.AddParameter("returnByValue", false);

        var errorDescription = string.Empty;
        var result = await _pageConnection.SendForResponseAsync(message, cancellationToken);
        var evaluateError = EvaluateError.FromJson(result);

        if (evaluateError.Result?.ExceptionDetails != null)
            errorDescription = evaluateError.Result.ExceptionDetails.Exception.Description;

        if (!string.IsNullOrEmpty(errorDescription))
            throw new ChromiumException(errorDescription);
    }
    #endregion

    #region CaptureSnapshotAsync
    /// <summary>
    ///     Instructs Chromium to capture a snapshot from the loaded page
    /// </summary>
    /// <param name="countdownTimer">
    ///     If a <see cref="CountdownTimer" /> is set then
    ///     the method will raise an <see cref="ConversionTimedOutException" /> in the
    ///     <see cref="CountdownTimer" /> reaches zero before finishing the printing to pdf
    /// </param>
    /// <param name="cancellationToken"><see cref="CancellationToken"/></param>
    /// <remarks>
    ///     See https://chromedevtools.github.io/devtools-protocol/tot/Page#method-captureSnapshot
    /// </remarks>
    /// <returns></returns>
    internal async Task<SnapshotResponse> CaptureSnapshotAsync(
        CountdownTimer countdownTimer = null, 
        CancellationToken cancellationToken = default)
    {
        var message = new Message { Method = "Page.captureSnapshot" };

        var result = countdownTimer == null
            ? await _pageConnection.SendForResponseAsync(message, cancellationToken)
            : await _pageConnection.SendForResponseAsync(message, cancellationToken).Timeout(countdownTimer.MillisecondsLeft);

        return SnapshotResponse.FromJson(result);
    }
    #endregion

    #region PrintToPdfAsync
    /// <summary>
    ///     Instructs Chromium to print the page
    /// </summary>
    /// <param name="outputStream">The generated PDF gets written to this stream</param>
    /// <param name="pageSettings">
    ///     <see cref="PageSettings" />
    /// </param>
    /// <param name="countdownTimer">
    ///     If a <see cref="CountdownTimer" /> is set then
    ///     the method will raise an <see cref="ConversionTimedOutException" /> in the
    ///     <see cref="CountdownTimer" /> reaches zero before finishing the printing to pdf
    /// </param>
    /// <param name="cancellationToken"><see cref="CancellationToken"/></param>
    /// <remarks>
    ///     See https://chromedevtools.github.io/devtools-protocol/tot/Page/#method-printToPDF
    /// </remarks>
    /// <exception cref="ConversionException">Raised when Chromium returns an empty string</exception>
    /// <exception cref="ConversionTimedOutException">Raised when <paramref name="countdownTimer" /> reaches zero</exception>
    internal async Task PrintToPdfAsync(Stream outputStream,
        PageSettings pageSettings,
        CountdownTimer countdownTimer = null,
        CancellationToken cancellationToken = default)
    {
        var message = new Message { Method = "Page.printToPDF" };
        message.AddParameter("landscape", pageSettings.Landscape);
        message.AddParameter("displayHeaderFooter", pageSettings.DisplayHeaderFooter);
        message.AddParameter("printBackground", pageSettings.PrintBackground);
        message.AddParameter("scale", pageSettings.Scale);
        message.AddParameter("paperWidth", pageSettings.PaperWidth);
        message.AddParameter("paperHeight", pageSettings.PaperHeight);
        message.AddParameter("marginTop", pageSettings.MarginTop);
        message.AddParameter("marginBottom", pageSettings.MarginBottom);
        message.AddParameter("marginLeft", pageSettings.MarginLeft);
        message.AddParameter("marginRight", pageSettings.MarginRight);
        message.AddParameter("pageRanges", pageSettings.PageRanges ?? string.Empty);
        message.AddParameter("ignoreInvalidPageRanges", pageSettings.IgnoreInvalidPageRanges);
        if (!string.IsNullOrEmpty(pageSettings.HeaderTemplate))
            message.AddParameter("headerTemplate", pageSettings.HeaderTemplate);
        if (!string.IsNullOrEmpty(pageSettings.FooterTemplate))
            message.AddParameter("footerTemplate", pageSettings.FooterTemplate);
        message.AddParameter("preferCSSPageSize", pageSettings.PreferCSSPageSize);
        message.AddParameter("transferMode", "ReturnAsStream");

        var result = countdownTimer == null
            ? await _pageConnection.SendForResponseAsync(message, cancellationToken)
            : await _pageConnection.SendForResponseAsync(message, cancellationToken).Timeout(countdownTimer.MillisecondsLeft);

        var printToPdfResponse = PrintToPdfResponse.FromJson(result);

        if (string.IsNullOrEmpty(printToPdfResponse.Result?.Stream))
            throw new ConversionException($"Conversion failed ... did not get the expected response from Chromium, response '{result}'");

        if (!outputStream.CanWrite)
            throw new ConversionException("The output stream is not writable, please provide a writable stream");

        WriteToLog("Resetting output stream to position 0");
        message = new Message { Method = "IO.read" };
        message.AddParameter("handle", printToPdfResponse.Result.Stream);
        message.AddParameter("size", 1048576); // Get the pdf in chunks of 1MB

        WriteToLog($"Reading generated PDF from IO stream with handle id {printToPdfResponse.Result.Stream}");
        outputStream.Position = 0;

        while (true)
        {
            result = countdownTimer == null
                ? await _pageConnection.SendForResponseAsync(message, cancellationToken)
                : await _pageConnection.SendForResponseAsync(message, cancellationToken).Timeout(countdownTimer.MillisecondsLeft);

            var ioReadResponse = IoReadResponse.FromJson(result);

            var bytes = ioReadResponse.Result.Bytes;
            var length = bytes.Length;

            if (length > 0)
            {
                WriteToLog($"PDF chunk received with id {ioReadResponse.Id} and length {length}, writing it to output stream");
                await outputStream.WriteAsync(bytes, 0, length, cancellationToken);
            }

            if (!ioReadResponse.Result.Eof) continue;

            WriteToLog("Last chunk received");
            WriteToLog($"Closing stream with id {printToPdfResponse.Result.Stream}");
            message = new Message { Method = "IO.close" };
            message.AddParameter("handle", printToPdfResponse.Result.Stream);
            await _pageConnection.SendForResponseAsync(message, cancellationToken);
            WriteToLog("Stream closed");
            break;
        }
    }
    #endregion

    #region CaptureScreenshotAsync
    /// <summary>
    ///     Instructs Chromium to take a screenshot from the page
    /// </summary>
    /// <param name="countdownTimer"></param>
    /// <param name="cancellationToken"><see cref="CancellationToken"/></param>
    /// <returns></returns>
    /// <exception cref="ConversionException">Raised when Chromium returns an empty string</exception>
    /// <exception cref="ConversionTimedOutException">Raised when <paramref name="countdownTimer" /> reaches zero</exception>
    internal async Task<CaptureScreenshotResponse> CaptureScreenshotAsync(
        CountdownTimer countdownTimer = null, 
        CancellationToken cancellationToken = default)
    {
        var message = new Message { Method = "Page.captureScreenshot" };
        var result = countdownTimer == null
            ? await _pageConnection.SendForResponseAsync(message, cancellationToken)
            : await _pageConnection.SendForResponseAsync(message, cancellationToken).Timeout(countdownTimer.MillisecondsLeft);

        var captureScreenshotResponse = CaptureScreenshotResponse.FromJson(result);

        if (string.IsNullOrEmpty(captureScreenshotResponse.Result?.Data))
            throw new ConversionException("Screenshot capture failed");

        return captureScreenshotResponse;
    }
    #endregion

    #region CloseAsync
    /// <summary>
    ///     Instructs the browser to close
    /// </summary>
    /// <param name="cancellationToken"><see cref="CancellationToken"/></param>
    /// <returns></returns>
    internal async Task CloseAsync(CancellationToken cancellationToken)
    {
        var message = new Message { Method = "Browser.close" };
        await _browserConnection.SendForResponseAsync(message, cancellationToken);
    }
    #endregion

    #region WriteToLog
    /// <summary>
    ///     Writes a line to the <see cref="_logger" />
    /// </summary>
    /// <param name="message">The message to write</param>
    internal void WriteToLog(string message)
    {
        lock (_lock)
        {
            try
            {
                if (_logger == null) return;
                using (_logger.BeginScope(InstanceId))
                {
                    _logger.LogInformation(message);
                }
            }
            catch (ObjectDisposedException)
            {
                // Ignore
            }
        }
    }
    #endregion

    #region Dispose
    /// <summary>
    ///     Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        if (_pageConnection != null)
            _pageConnection.OnError -= OnOnError;

        if (_browserConnection != null)
            _browserConnection.OnError -= OnOnError;

        CloseAsync(CancellationToken.None).ConfigureAwait(false).GetAwaiter().GetResult();

        if (_pageConnection != null)
        {
            _pageConnection.Dispose();
            _pageConnection = null;
        }

        if (_browserConnection != null)
        {
            _browserConnection.Dispose();
            _browserConnection = null;
        }

        _disposed = true;
    }
    #endregion

    #region DisposeAsync
#if (!NETSTANDARD2_0)    
    /// <summary>
    ///     Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        if (_pageConnection != null)
            _pageConnection.OnError -= OnOnError;

        if (_browserConnection != null)
            _browserConnection.OnError -= OnOnError;

        await CloseAsync(CancellationToken.None);

        if (_pageConnection != null)
        {
            await _pageConnection.DisposeAsync();
            _pageConnection = null;
        }

        if (_browserConnection != null)
        {
            await _browserConnection.DisposeAsync();
            _browserConnection = null;
        }

        _disposed = true;
    }
#endif
    #endregion
}