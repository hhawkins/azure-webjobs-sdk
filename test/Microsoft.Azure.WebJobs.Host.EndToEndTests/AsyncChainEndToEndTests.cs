﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs.Host.Executors;
using Microsoft.Azure.WebJobs.Host.Loggers;
using Microsoft.Azure.WebJobs.Host.Protocols;
using Microsoft.Azure.WebJobs.Host.Queues;
using Microsoft.Azure.WebJobs.Host.TestCommon;
using Microsoft.Azure.WebJobs.Host.Timers;
using Microsoft.Azure.WebJobs.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using Microsoft.WindowsAzure.Storage.Queue;
using Microsoft.WindowsAzure.Storage.Table;
using Xunit;

namespace Microsoft.Azure.WebJobs.Host.EndToEndTests
{
    public class AsyncChainEndToEndTests : IClassFixture<AsyncChainEndToEndTests.TestFixture>
    {
        private const string TestArtifactsPrefix = "asynce2e";

        private const string ContainerName = TestArtifactsPrefix + "%rnd%";

        private const string NonWebJobsBlobName = "NonWebJobs";
        private const string Blob1Name = "Blob1";
        private const string Blob2Name = "Blob2";

        private const string Queue1Name = TestArtifactsPrefix + "q1%rnd%";
        private const string Queue2Name = TestArtifactsPrefix + "q2%rnd%";
        private const string TestQueueName = TestArtifactsPrefix + "q3%rnd%";

        private static CloudStorageAccount _storageAccount;

        private static RandomNameResolver _resolver;
        private static JobHostConfiguration _hostConfig;
        private readonly TestExceptionHandler _defaultExceptionHandler;
        private static EventWaitHandle _functionCompletedEvent;

        private static string _finalBlobContent;
        private static TimeSpan _timeoutJobDelay;

        private readonly CloudQueue _testQueue;
        private readonly TestFixture _fixture;

        private readonly TestLoggerProvider _loggerProvider = new TestLoggerProvider();

        public AsyncChainEndToEndTests(TestFixture fixture)
        {
            _fixture = fixture;
            _resolver = new RandomNameResolver();
            _hostConfig = new JobHostConfiguration()
            {
                NameResolver = _resolver,
                TypeLocator = new FakeTypeLocator(typeof(AsyncChainEndToEndTests))
            };

            _defaultExceptionHandler = new TestExceptionHandler();
            _hostConfig.AddService<IWebJobsExceptionHandler>(_defaultExceptionHandler);
            _hostConfig.Queues.MaxPollingInterval = TimeSpan.FromSeconds(2);

            _storageAccount = fixture.StorageAccount;
            _timeoutJobDelay = TimeSpan.FromMinutes(5);

            ILoggerFactory loggerFactory = new LoggerFactory();
            loggerFactory.AddProvider(_loggerProvider);
            _hostConfig.LoggerFactory = loggerFactory;
            _hostConfig.Aggregator.IsEnabled = false; // makes validation easier

            CloudQueueClient queueClient = _storageAccount.CreateCloudQueueClient();
            string queueName = _resolver.ResolveInString(TestQueueName);
            _testQueue = queueClient.GetQueueReference(queueName);
            if (!_testQueue.CreateIfNotExistsAsync().Result)
            {
                _testQueue.ClearAsync().Wait();
            }
        }

        [Fact]
        public async Task AsyncChainEndToEnd()
        {
            using (_functionCompletedEvent = new ManualResetEvent(initialState: false))
            {
                TextWriter hold = Console.Out;
                StringWriter consoleOutput = new StringWriter();
                Console.SetOut(consoleOutput);

                await AsyncChainEndToEndInternal();

                Console.SetOut(hold);

                string firstQueueName = _resolver.ResolveInString(Queue1Name);
                string secondQueueName = _resolver.ResolveInString(Queue2Name);
                string blobContainerName = _resolver.ResolveInString(ContainerName);
                string[] consoleOutputLines = consoleOutput.ToString().Trim().Split(new string[] { Environment.NewLine }, StringSplitOptions.None).OrderBy(p => p).ToArray();
                string[] expectedOutputLines = new string[]
                {
                    "Found the following functions:",
                    "Microsoft.Azure.WebJobs.Host.EndToEndTests.AsyncChainEndToEndTests.WriteStartDataMessageToQueue",
                    "Microsoft.Azure.WebJobs.Host.EndToEndTests.AsyncChainEndToEndTests.QueueToQueueAsync",
                    "Microsoft.Azure.WebJobs.Host.EndToEndTests.AsyncChainEndToEndTests.QueueToBlobAsync",
                    "Microsoft.Azure.WebJobs.Host.EndToEndTests.AsyncChainEndToEndTests.AlwaysFailJob",
                    "Microsoft.Azure.WebJobs.Host.EndToEndTests.AsyncChainEndToEndTests.DisabledJob",
                    "Microsoft.Azure.WebJobs.Host.EndToEndTests.AsyncChainEndToEndTests.QueueTrigger_TraceLevelOverride",
                    "Microsoft.Azure.WebJobs.Host.EndToEndTests.AsyncChainEndToEndTests.TimeoutJob",
                    "Microsoft.Azure.WebJobs.Host.EndToEndTests.AsyncChainEndToEndTests.TimeoutJob_Throw",
                    "Microsoft.Azure.WebJobs.Host.EndToEndTests.AsyncChainEndToEndTests.TimeoutJob_Throw_NoToken",
                    "Microsoft.Azure.WebJobs.Host.EndToEndTests.AsyncChainEndToEndTests.BlobToBlobAsync",
                    "Microsoft.Azure.WebJobs.Host.EndToEndTests.AsyncChainEndToEndTests.ReadResultBlob",
                    "Microsoft.Azure.WebJobs.Host.EndToEndTests.AsyncChainEndToEndTests.SystemParameterBindingOutput",
                    "Function 'AsyncChainEndToEndTests.DisabledJob' is disabled",
                    "Job host started",
                    "Executing 'AsyncChainEndToEndTests.WriteStartDataMessageToQueue' (Reason='This function was programmatically called via the host APIs.', Id=",
                    "Executed 'AsyncChainEndToEndTests.WriteStartDataMessageToQueue' (Succeeded, Id=",
                    string.Format("Executing 'AsyncChainEndToEndTests.QueueToQueueAsync' (Reason='New queue message detected on '{0}'.', Id=", firstQueueName),
                    "Executed 'AsyncChainEndToEndTests.QueueToQueueAsync' (Succeeded, Id=",
                    string.Format("Executing 'AsyncChainEndToEndTests.QueueToBlobAsync' (Reason='New queue message detected on '{0}'.', Id=", secondQueueName),
                    "Executed 'AsyncChainEndToEndTests.QueueToBlobAsync' (Succeeded, Id=",
                    string.Format("Executing 'AsyncChainEndToEndTests.BlobToBlobAsync' (Reason='New blob detected: {0}/Blob1', Id=", blobContainerName),
                    "Executed 'AsyncChainEndToEndTests.BlobToBlobAsync' (Succeeded, Id=",
                    "Job host stopped",
                    "Executing 'AsyncChainEndToEndTests.ReadResultBlob' (Reason='This function was programmatically called via the host APIs.', Id=",
                    "Executed 'AsyncChainEndToEndTests.ReadResultBlob' (Succeeded, Id=",
                    "User TraceWriter log",
                    "Another User TextWriter log",
                    "User TextWriter log (TestParam)"
                }.OrderBy(p => p).ToArray();

                bool hasError = consoleOutputLines.Any(p => p.Contains("Function had errors"));
                Assert.False(hasError);

                // Validate console output
                for (int i = 0; i < expectedOutputLines.Length; i++)
                {
                    Assert.StartsWith(expectedOutputLines[i], consoleOutputLines[i]);
                }

                // Validate Logger output
                var allLogs = _loggerProvider.CreatedLoggers.SelectMany(l => l.LogMessages.SelectMany(m => m.FormattedMessage.Trim().Split(new string[] { Environment.NewLine }, StringSplitOptions.None))).OrderBy(p => p).ToArray();
                // Logger doesn't log the 'Executing' messages
                var loggerExpected = expectedOutputLines.Where(l => !l.StartsWith("Executing '")).ToArray();

                for (int i = 0; i < loggerExpected.Length; i++)
                {
                    Assert.StartsWith(loggerExpected[i], allLogs[i]);
                }
            }
        }

        [Fact]
        public async Task AsyncChainEndToEnd_CustomFactories()
        {
            using (_functionCompletedEvent = new ManualResetEvent(initialState: false))
            {
                CustomQueueProcessorFactory queueProcessorFactory = new CustomQueueProcessorFactory();
                _hostConfig.Queues.QueueProcessorFactory = queueProcessorFactory;

                CustomStorageClientFactory storageClientFactory = new CustomStorageClientFactory();
                _hostConfig.StorageClientFactory = storageClientFactory;

                await AsyncChainEndToEndInternal();

                Assert.Equal(3, queueProcessorFactory.CustomQueueProcessors.Count);
                Assert.True(queueProcessorFactory.CustomQueueProcessors.All(p => p.Context.Queue.Name.StartsWith("asynce2eq")));
                Assert.True(queueProcessorFactory.CustomQueueProcessors.Sum(p => p.BeginProcessingCount) >= 2);
                Assert.True(queueProcessorFactory.CustomQueueProcessors.Sum(p => p.CompleteProcessingCount) >= 2);

                Assert.Equal(19, storageClientFactory.TotalBlobClientCount);
                Assert.Equal(15, storageClientFactory.TotalQueueClientCount);
                Assert.Equal(0, storageClientFactory.TotalTableClientCount);

                Assert.Equal(8, storageClientFactory.ParameterBlobClientCount);
                Assert.Equal(5, storageClientFactory.ParameterQueueClientCount);
                Assert.Equal(0, storageClientFactory.ParameterTableClientCount);
            }
        }

        [Fact]
        public async Task TraceWriterLogging()
        {
            TextWriter hold = Console.Out;
            StringWriter consoleOutput = new StringWriter();
            Console.SetOut(consoleOutput);

            using (_functionCompletedEvent = new ManualResetEvent(initialState: false))
            {
                TestTraceWriter trace = new TestTraceWriter(TraceLevel.Verbose);
                _hostConfig.Tracing.Tracers.Add(trace);
                JobHost host = new JobHost(_hostConfig);

                await host.StartAsync();
                await host.CallAsync(typeof(AsyncChainEndToEndTests).GetMethod("WriteStartDataMessageToQueue"));

                await TestHelpers.Await(() => _functionCompletedEvent.WaitOne(200), 30000);

                // ensure all logs have had a chance to flush
                await Task.Delay(3000);

                await host.StopAsync();

                bool hasError = string.Join(Environment.NewLine, trace.Traces.Where(p => p.Message.Contains("Error"))).Any();
                Assert.False(hasError);

                Assert.NotNull(trace.Traces.SingleOrDefault(p => p.Message.Contains("User TraceWriter log")));
                Assert.NotNull(trace.Traces.SingleOrDefault(p => p.Message.Contains("User TextWriter log (TestParam)")));
                Assert.NotNull(trace.Traces.SingleOrDefault(p => p.Message.Contains("Another User TextWriter log")));
                ValidateTraceProperties(trace);

                string[] consoleOutputLines = consoleOutput.ToString().Trim().Split(new string[] { Environment.NewLine }, StringSplitOptions.None);
                Assert.NotNull(consoleOutputLines.SingleOrDefault(p => p.Contains("User TraceWriter log")));
                Assert.NotNull(consoleOutputLines.SingleOrDefault(p => p.Contains("User TextWriter log (TestParam)")));
                Assert.NotNull(consoleOutputLines.SingleOrDefault(p => p.Contains("Another User TextWriter log")));

                // Validate Logger
                var logger = _loggerProvider.CreatedLoggers.Where(l => l.Category == LogCategories.Function).Single();
                Assert.Equal(3, logger.LogMessages.Count);
                Assert.NotNull(logger.LogMessages.SingleOrDefault(p => p.FormattedMessage.Contains("User TraceWriter log")));
                Assert.NotNull(logger.LogMessages.SingleOrDefault(p => p.FormattedMessage.Contains("User TextWriter log (TestParam)")));
                Assert.NotNull(logger.LogMessages.SingleOrDefault(p => p.FormattedMessage.Contains("Another User TextWriter log")));
            }

            Console.SetOut(hold);
        }

        [Fact]
        public async Task AggregatorAndEventCollector()
        {
            using (_functionCompletedEvent = new ManualResetEvent(initialState: false))
            {
                _hostConfig.Tracing.ConsoleLevel = TraceLevel.Off;

                // enable the aggregator
                _hostConfig.Aggregator.IsEnabled = true;
                _hostConfig.Aggregator.BatchSize = 1;

                // add a FunctionEventCollector
                var eventCollector = new TestFunctionEventCollector();
                _hostConfig.AddService<IAsyncCollector<FunctionInstanceLogEntry>>(eventCollector);

                JobHost host = new JobHost(_hostConfig);

                await host.StartAsync();
                await host.CallAsync(typeof(AsyncChainEndToEndTests).GetMethod("WriteStartDataMessageToQueue"));

                await TestHelpers.Await(()=>_functionCompletedEvent.WaitOne(200), 30000);
                
                // ensure all logs have had a chance to flush
                await Task.Delay(3000);

                await host.StopAsync();

                // Make sure the aggregator was logged to
                var logger = _loggerProvider.CreatedLoggers.Where(l => l.Category == LogCategories.Aggregator).Single();
                Assert.Equal(4, logger.LogMessages.Count);

                // Make sure the eventCollector was logged 
                eventCollector.AssertFunctionCount(4);
            }
        }

        [Fact]
        public async Task AggregatorOnly()
        {
            using (_functionCompletedEvent = new ManualResetEvent(initialState: false))
            {
                _hostConfig.Tracing.ConsoleLevel = TraceLevel.Off;

                // enable the aggregator
                _hostConfig.Aggregator.IsEnabled = true;
                _hostConfig.Aggregator.BatchSize = 1;

                JobHost host = new JobHost(_hostConfig);

                await host.StartAsync();
                await host.CallAsync(typeof(AsyncChainEndToEndTests).GetMethod("WriteStartDataMessageToQueue"));

                await TestHelpers.Await(() => _functionCompletedEvent.WaitOne(200), 30000);

                // ensure all logs have had a chance to flush
                await Task.Delay(3000);

                await host.StopAsync();

                // Make sure the aggregator was logged to
                var logger = _loggerProvider.CreatedLoggers.Where(l => l.Category == LogCategories.Aggregator).Single();
                Assert.Equal(4, logger.LogMessages.Count);
            }
        }

        [Fact]
        public async Task EventCollectorOnly()
        {
            using (_functionCompletedEvent = new ManualResetEvent(initialState: false))
            {
                _hostConfig.Tracing.ConsoleLevel = TraceLevel.Off;

                // disable the aggregator
                _hostConfig.Aggregator.IsEnabled = false;

                // add a FunctionEventCollector
                var eventCollector = new TestFunctionEventCollector();
                _hostConfig.AddService<IAsyncCollector<FunctionInstanceLogEntry>>(eventCollector);

                JobHost host = new JobHost(_hostConfig);

                await host.StartAsync();
                await host.CallAsync(typeof(AsyncChainEndToEndTests).GetMethod("WriteStartDataMessageToQueue"));

                await TestHelpers.Await(() => _functionCompletedEvent.WaitOne(200), 30000);

                // ensure all logs have had a chance to flush
                await Task.Delay(3000);

                await host.StopAsync();

                // Make sure the aggregator was logged to
                var logger = _loggerProvider.CreatedLoggers.Where(l => l.Category == LogCategories.Aggregator).SingleOrDefault();
                Assert.Null(logger);

                // Make sure the eventCollector was logged
                eventCollector.AssertFunctionCount(4);
            }
        }

        private void ValidateTraceProperties(TestTraceWriter trace)
        {
            foreach (var traceEvent in trace.Traces)
            {
                var message = traceEvent.Message;
                var startedOrEndedMessage = message.StartsWith("Executing ") || message.StartsWith("Executed ");
                var userMessage = message.Contains("User TextWriter") || message.Contains("User TraceWriter");

                if (startedOrEndedMessage || userMessage)
                {
                    Assert.Equal(3, traceEvent.Properties.Count);

                    Assert.IsType<Guid>(traceEvent.Properties["MS_HostInstanceId"]);
                    Assert.IsType<Guid>(traceEvent.Properties["MS_FunctionInvocationId"]);

                    if (startedOrEndedMessage)
                    {
                        // Validate that the FunctionDescriptor looks right
                        var start = message.IndexOf("'") + 1;
                        var end = message.IndexOf("'", start) - start;
                        var functionName = message.Substring(start, end);
                        var descriptor = (FunctionDescriptor)traceEvent.Properties["MS_FunctionDescriptor"];
                        Assert.Equal(functionName, descriptor.ShortName);
                    }
                }
                else
                {
                    Assert.Equal(0, traceEvent.Properties.Count);
                }
            }
        }

        [Fact]
        public void FunctionFailures_LogsExpectedTraceEvent()
        {
            TestTraceWriter trace = new TestTraceWriter(TraceLevel.Verbose);
            _hostConfig.Tracing.Tracers.Add(trace);
            JobHost host = new JobHost(_hostConfig);

            MethodInfo methodInfo = GetType().GetMethod("AlwaysFailJob");
            try
            {
                host.Call(methodInfo);
            }
            catch { }

            string expectedName = $"{methodInfo.DeclaringType.FullName}.{methodInfo.Name}";

            // Validate TraceWriter
            // We expect 3 error messages total
            TraceEvent[] traceErrors = trace.Traces.Where(p => p.Level == TraceLevel.Error).ToArray();
            Assert.Equal(3, traceErrors.Length);

            // Ensure that all errors include the same exception, with function
            // invocation details           
            FunctionInvocationException functionException = traceErrors.First().Exception as FunctionInvocationException;
            Assert.NotNull(functionException);
            Assert.NotEqual(Guid.Empty, functionException.InstanceId);
            Assert.Equal(expectedName, functionException.MethodName);
            Assert.True(traceErrors.All(p => functionException == p.Exception));

            // Validate Logger
            // Logger only writes out a single log message (which includes the Exception).        
            var logger = _loggerProvider.CreatedLoggers.Where(l => l.Category == LogCategories.Results).Single();
            var logMessage = logger.LogMessages.Single();
            var loggerException = logMessage.Exception as FunctionException;
            Assert.NotNull(loggerException);
            Assert.Equal(expectedName, loggerException.MethodName);
        }

        [Fact]
        public async Task SystemParameterBindingOutput_GeneratesExpectedBlobs()
        {
            JobHost host = new JobHost(_hostConfig);

            var blobClient = _fixture.StorageAccount.CreateCloudBlobClient();
            var container = blobClient.GetContainerReference("test-output");
            if (await container.ExistsAsync())
            {
                foreach (CloudBlockBlob blob in (await container.ListBlobsSegmentedAsync(null)).Results)
                {
                    await blob.DeleteAsync();
                }
            }

            MethodInfo methodInfo = GetType().GetMethod("SystemParameterBindingOutput");
            var arguments = new Dictionary<string, object>
            {
                { "input", "Test Value" }
            };
            host.Call(methodInfo, arguments);

            // We expect 3 separate blobs to have been written
            var blobs = (await container.ListBlobsSegmentedAsync(null)).Results.Cast<CloudBlockBlob>().ToArray();
            Assert.Equal(3, blobs.Length);
            foreach (var blob in blobs)
            {
                string content = await blob.DownloadTextAsync();
                Assert.Equal("Test Value", content.Trim(new char[] { '\uFEFF', '\u200B' }));
            }
        }

        [Fact]
        public async Task Timeout_TimeoutExpires_Cancels()
        {
            var exceptionHandler = new TestExceptionHandler();
            await RunTimeoutTest(exceptionHandler, typeof(TaskCanceledException), "TimeoutJob");
            Assert.Empty(exceptionHandler.UnhandledExceptionInfos);
            Assert.Empty(exceptionHandler.TimeoutExceptionInfos);
        }

        [Fact]
        public async Task TimeoutWithThrow_TimeoutExpires_CancelsAndThrows()
        {
            var exceptionHandler = new TestExceptionHandler();
            await RunTimeoutTest(exceptionHandler, typeof(FunctionTimeoutException), "TimeoutJob_Throw");
            var exception = exceptionHandler.TimeoutExceptionInfos.Single().SourceException;
            Assert.IsType<FunctionTimeoutException>(exception);
            Assert.Empty(exceptionHandler.UnhandledExceptionInfos);
        }

        [Fact]
        public async Task TimeoutWithThrow_NoCancellationToken_CancelsAndThrows()
        {
            var exceptionHandler = new TestExceptionHandler();
            await RunTimeoutTest(exceptionHandler, typeof(FunctionTimeoutException), "TimeoutJob_Throw_NoToken");
            var exception = exceptionHandler.TimeoutExceptionInfos.Single().SourceException;
            Assert.IsType<FunctionTimeoutException>(exception);
            Assert.Empty(exceptionHandler.UnhandledExceptionInfos);
        }

        private async Task RunTimeoutTest(IWebJobsExceptionHandler exceptionHandler, Type expectedExceptionType, string functionName)
        {
            try
            {
                TestTraceWriter trace = new TestTraceWriter(TraceLevel.Verbose);
                _hostConfig.Tracing.Tracers.Add(trace);
                _hostConfig.AddService<IWebJobsExceptionHandler>(exceptionHandler);
                JobHost host = new JobHost(_hostConfig);

                try
                {
                    await host.StartAsync();

                    MethodInfo methodInfo = GetType().GetMethod(functionName);
                    Exception ex = await Assert.ThrowsAnyAsync<Exception>(async () =>
                    {
                        await host.CallAsync(methodInfo);
                    });

                    Assert.IsType(expectedExceptionType, ex);
                }
                finally
                {
                    host.Stop();
                }

                string expectedExceptionMessage = $"Timeout value of 00:00:01 exceeded by function 'AsyncChainEndToEndTests.{functionName}'";
                string expectedResultMessage = $"Executed 'AsyncChainEndToEndTests.{functionName}' (Failed, Id=";

                // Validate TraceWriter
                // We expect 3 error messages total
                TraceEvent[] traceErrors = trace.Traces.Where(p => p.Level == TraceLevel.Error).ToArray();
                Assert.Equal(3, traceErrors.Length);
                Assert.StartsWith(expectedExceptionMessage, traceErrors[0].Message);
                Assert.StartsWith(expectedResultMessage, traceErrors[1].Message);
                Assert.StartsWith("Function had errors. See Azure WebJobs SDK dashboard for details.", traceErrors[2].Message.Trim());

                // Validate Logger
                // One error is logged by the Executor and one as a Result.
                var resultLogger = _loggerProvider.CreatedLoggers.Where(l => l.Category == LogCategories.Results).Single();
                var executorLogger = _loggerProvider.CreatedLoggers.Where(l => l.Category == LogCategories.Executor).Single();
                Assert.NotNull(resultLogger.LogMessages.Single().Exception);
                Assert.StartsWith(expectedResultMessage, resultLogger.LogMessages.Single().FormattedMessage);
                Assert.StartsWith(expectedExceptionMessage, executorLogger.LogMessages.Single().FormattedMessage);
            }
            finally
            {
                _hostConfig.AddService<IWebJobsExceptionHandler>(_defaultExceptionHandler);
            }
        }

        [Fact]
        public async Task Timeout_NoExpiry_CompletesSuccessfully()
        {
            TestTraceWriter trace = new TestTraceWriter(TraceLevel.Verbose);
            _hostConfig.Tracing.Tracers.Add(trace);
            JobHost host = new JobHost(_hostConfig);

            _timeoutJobDelay = TimeSpan.FromSeconds(0);
            MethodInfo methodInfo = GetType().GetMethod("TimeoutJob");
            await host.CallAsync(methodInfo);

            // Validate TraceWriter
            TraceEvent[] traceErrors = trace.Traces.Where(p => p.Level == TraceLevel.Error).ToArray();
            Assert.Equal(0, traceErrors.Length);

            // Validate Logger
            LogMessage[] logErrors = _loggerProvider.GetAllLogMessages().Where(l => l.Level == Extensions.Logging.LogLevel.Error).ToArray();
            Assert.Equal(0, logErrors.Length);
        }

        [Fact]
        public async Task FunctionTraceLevelOverride_ProducesExpectedOutput()
        {
            TestTraceWriter trace = new TestTraceWriter(TraceLevel.Verbose);
            _hostConfig.Tracing.Tracers.Add(trace);
            JobHost host = new JobHost(_hostConfig);

            try
            {
                using (_functionCompletedEvent = new ManualResetEvent(initialState: false))
                {
                    await host.StartAsync();

                    CloudQueueMessage message = new CloudQueueMessage("test message");
                    await _testQueue.AddMessageAsync(message);

                    await TestHelpers.Await(() => _functionCompletedEvent.WaitOne(200), 30000);

                    // wait for logs to flush
                    await Task.Delay(3000);

                    // expect no function output
                    TraceEvent[] traces = trace.Traces.ToArray();
                    Assert.Equal(4, traces.Length);
                    Assert.False(traces.Any(p => p.Message.Contains("test message")));
                }
            }
            finally
            {
                host.Stop();
            }
        }

        [Fact]
        public async Task FunctionTraceLevelOverride_Failure_ProducesExpectedOutput()
        {
            TestTraceWriter trace = new TestTraceWriter(TraceLevel.Verbose);
            _hostConfig.Tracing.Tracers.Add(trace);
            _hostConfig.Queues.MaxDequeueCount = 1;
            JobHost host = new JobHost(_hostConfig);

            try
            {
                using (_functionCompletedEvent = new ManualResetEvent(initialState: false))
                {
                    await host.StartAsync();

                    CloudQueueMessage message = new CloudQueueMessage("throw_message");
                    await _testQueue.AddMessageAsync(message);

                    await TestHelpers.Await(() => _functionCompletedEvent.WaitOne(200), 30000);

                    // wait for logs to flush
                    await Task.Delay(3000);

                    // expect normal logs to be written (TraceLevel override is ignored)
                    TraceEvent[] traces = trace.Traces.ToArray();
                    Assert.Equal(9, traces.Length);

                    string output = string.Join("\r\n", traces.Select(p => p.Message));
                    Assert.Contains("Executing 'AsyncChainEndToEndTests.QueueTrigger_TraceLevelOverride' (Reason='New queue message detected", output);
                    Assert.Contains("Exception while executing function: AsyncChainEndToEndTests.QueueTrigger_TraceLevelOverride", output);
                    Assert.Contains("Executed 'AsyncChainEndToEndTests.QueueTrigger_TraceLevelOverride' (Failed, Id=", output);
                    Assert.Contains("Message has reached MaxDequeueCount of 1", output);
                }
            }
            finally
            {
                host.Stop();
            }
        }

        [NoAutomaticTrigger]
        public static async Task WriteStartDataMessageToQueue(
            [Queue(Queue1Name)] ICollector<string> queueMessages,
            [Blob(ContainerName + "/" + NonWebJobsBlobName, FileAccess.Write)] Stream nonSdkBlob,
            CancellationToken token)
        {
            queueMessages.Add(" works");

            byte[] messageBytes = Encoding.UTF8.GetBytes("async");
            await nonSdkBlob.WriteAsync(messageBytes, 0, messageBytes.Length);
        }

        [NoAutomaticTrigger]
        public static void AlwaysFailJob()
        {
            throw new Exception("Kaboom!");
        }

        [NoAutomaticTrigger]
        public static void SystemParameterBindingOutput(
            [QueueTrigger("test")] string input,
            [Blob("test-output/{rand-guid}")] out string blob,
            [Blob("test-output/{rand-guid:N}")] out string blob2,
            [Blob("test-output/{datetime:yyyy-mm-dd}:{rand-guid:N}")] out string blob3)
        {
            blob = blob2 = blob3 = input;
        }

        [Disable("Disable_DisabledJob")]
        public static void DisabledJob([QueueTrigger(Queue1Name)] string message)
        {
        }

        [NoAutomaticTrigger]
        [Timeout("00:00:01", TimeoutWhileDebugging = true)]
        public static async Task TimeoutJob(CancellationToken cancellationToken, TextWriter log)
        {
            log.WriteLine("Started");
            await Task.Delay(_timeoutJobDelay, cancellationToken);
            log.WriteLine("Completed");
        }

        [NoAutomaticTrigger]
        [Timeout("00:00:01", ThrowOnTimeout = true, TimeoutWhileDebugging = true)]
        public static async Task TimeoutJob_Throw(CancellationToken cancellationToken, TextWriter log)
        {
            log.WriteLine("Started");
            await Task.Delay(_timeoutJobDelay, cancellationToken);
            log.WriteLine("Completed");
        }

        [NoAutomaticTrigger]
        [Timeout("00:00:01", ThrowOnTimeout = true, TimeoutWhileDebugging = true)]
        public static async Task TimeoutJob_Throw_NoToken(TextWriter log)
        {
            log.WriteLine("Started");
            await Task.Delay(_timeoutJobDelay);
            log.WriteLine("Completed");
        }

        [TraceLevel(TraceLevel.Error)]
        public static void QueueTrigger_TraceLevelOverride(
            [QueueTrigger(TestQueueName)] string message, TextWriter log)
        {
            log.WriteLine(message);

            _functionCompletedEvent.Set();

            if (message == "throw_message")
            {
                throw new Exception("Kaboom!");
            }
        }

        public static async Task QueueToQueueAsync(
            [QueueTrigger(Queue1Name)] string message,
            [Queue(Queue2Name)] IAsyncCollector<string> output,
            CancellationToken token,
            TraceWriter trace)
        {
            CloudBlobClient blobClient = _storageAccount.CreateCloudBlobClient();
            CloudBlobContainer container = blobClient.GetContainerReference(_resolver.ResolveInString(ContainerName));
            CloudBlockBlob blob = container.GetBlockBlobReference(NonWebJobsBlobName);
            string blobContent = await blob.DownloadTextAsync();

            trace.Info("User TraceWriter log");

            await output.AddAsync(blobContent + message);
        }

        public static async Task QueueToBlobAsync(
            [QueueTrigger(Queue2Name)] string message,
            [Blob(ContainerName + "/" + Blob1Name, FileAccess.Write)] Stream blobStream,
            CancellationToken token,
            TextWriter log)
        {
            byte[] messageBytes = Encoding.UTF8.GetBytes(message);

            log.WriteLine("User TextWriter log ({0})", "TestParam");
            log.Write("Another User TextWriter log");

            await blobStream.WriteAsync(messageBytes, 0, messageBytes.Length);
        }

        public static async Task BlobToBlobAsync(
            [BlobTrigger(ContainerName + "/" + Blob1Name)] Stream inputStream,
            string blobTrigger,
            Uri uri,
            IDictionary<string, string> metadata,
            BlobProperties properties,
            [Blob(ContainerName + "/" + Blob2Name, FileAccess.Write)] Stream outputStream,
            CancellationToken token)
        {
            Assert.True(uri.ToString().EndsWith(blobTrigger));
            string parentId = metadata["AzureWebJobsParentId"];
            Guid g;
            Assert.True(Guid.TryParse(parentId, out g));
            Assert.Equal("application/octet-stream", properties.ContentType);

            // Should not be signaled
            if (token.IsCancellationRequested)
            {
                _functionCompletedEvent.Set();
                return;
            }

            await inputStream.CopyToAsync(outputStream);
            outputStream.Close();

            _functionCompletedEvent.Set();
        }

        public static void ReadResultBlob(
            [Blob(ContainerName + "/" + Blob2Name)] string blob,
            CancellationToken token)
        {
            // Should not be signaled
            if (token.IsCancellationRequested)
            {
                return;
            }

            _finalBlobContent = blob;
        }

        private async Task AsyncChainEndToEndInternal()
        {
            JobHost host = new JobHost(_hostConfig);

            Assert.Null(_hostConfig.HostId);

            await host.StartAsync();

            Assert.NotEmpty(_hostConfig.HostId);

            await host.CallAsync(typeof(AsyncChainEndToEndTests).GetMethod("WriteStartDataMessageToQueue"));

            await TestHelpers.Await(() => _functionCompletedEvent.WaitOne(200), 30000);

            // ensure all logs have had a chance to flush
            await Task.Delay(3000);

            // Stop async waits for the function to complete
            await host.StopAsync();

            await host.CallAsync(typeof(AsyncChainEndToEndTests).GetMethod("ReadResultBlob"));
            Assert.Equal("async works", _finalBlobContent);
        }

        private class CustomQueueProcessorFactory : IQueueProcessorFactory
        {
            public List<CustomQueueProcessor> CustomQueueProcessors = new List<CustomQueueProcessor>();

            public QueueProcessor Create(QueueProcessorFactoryContext context)
            {
                // demonstrates how the Queue.ServiceClient options can be configured
                context.Queue.ServiceClient.DefaultRequestOptions.ServerTimeout = TimeSpan.FromSeconds(30);

                // demonstrates how queue options can be customized
                context.Queue.EncodeMessage = true;

                // demonstrates how batch processing behavior and other knobs
                // can be customized
                context.BatchSize = 30;
                context.NewBatchThreshold = 100;
                context.MaxPollingInterval = TimeSpan.FromSeconds(15);

                CustomQueueProcessor processor = new CustomQueueProcessor(context);
                CustomQueueProcessors.Add(processor);
                return processor;
            }
        }

        public class CustomQueueProcessor : QueueProcessor
        {
            public int BeginProcessingCount = 0;
            public int CompleteProcessingCount = 0;

            public CustomQueueProcessor(QueueProcessorFactoryContext context) : base(context)
            {
                Context = context;
            }

            public QueueProcessorFactoryContext Context { get; private set; }

            public override Task<bool> BeginProcessingMessageAsync(CloudQueueMessage message, CancellationToken cancellationToken)
            {
                BeginProcessingCount++;
                return base.BeginProcessingMessageAsync(message, cancellationToken);
            }

            public override Task CompleteProcessingMessageAsync(CloudQueueMessage message, FunctionResult result, CancellationToken cancellationToken)
            {
                CompleteProcessingCount++;
                return base.CompleteProcessingMessageAsync(message, result, cancellationToken);
            }

            protected override async Task ReleaseMessageAsync(CloudQueueMessage message, FunctionResult result, TimeSpan visibilityTimeout, CancellationToken cancellationToken)
            {
                // demonstrates how visibility timeout for failed messages can be customized
                // the logic here could implement exponential backoff, etc.
                visibilityTimeout = TimeSpan.FromSeconds(message.DequeueCount);

                await base.ReleaseMessageAsync(message, result, visibilityTimeout, cancellationToken);
            }
        }

        /// <summary>
        /// This custom <see cref="StorageClientFactory"/> demonstrates how clients can be customized.
        /// For example, users can configure global retry policies, DefaultRequestOptions, etc.
        /// </summary>
        public class CustomStorageClientFactory : StorageClientFactory
        {
            public int TotalBlobClientCount;
            public int TotalQueueClientCount;
            public int TotalTableClientCount;

            public int ParameterBlobClientCount;
            public int ParameterQueueClientCount;
            public int ParameterTableClientCount;

            public override CloudBlobClient CreateCloudBlobClient(StorageClientFactoryContext context)
            {
                TotalBlobClientCount++;

                if (context.Parameter != null)
                {
                    ParameterBlobClientCount++;
                }

                return base.CreateCloudBlobClient(context);
            }

            public override CloudQueueClient CreateCloudQueueClient(StorageClientFactoryContext context)
            {
                TotalQueueClientCount++;

                if (context.Parameter != null)
                {
                    ParameterQueueClientCount++;

                    if (context.Parameter.Member.Name == "QueueToQueueAsync")
                    {
                        // demonstrates how context can be used to create a custom client
                        // for a particular method or parameter binding
                    }
                }

                return base.CreateCloudQueueClient(context);
            }

            public override CloudTableClient CreateCloudTableClient(StorageClientFactoryContext context)
            {
                TotalTableClientCount++;

                if (context.Parameter != null)
                {
                    ParameterTableClientCount++;
                }

                return base.CreateCloudTableClient(context);
            }
        }

        public class TestFixture : IDisposable
        {
            public TestFixture()
            {
                JobHostConfiguration config = new JobHostConfiguration();
                StorageAccount = CloudStorageAccount.Parse(config.StorageConnectionString);
            }

            public CloudStorageAccount StorageAccount
            {
                get;
                private set;
            }

            public void Dispose()
            {
                CloudBlobClient blobClient = StorageAccount.CreateCloudBlobClient();
                foreach (var testContainer in blobClient.ListContainersSegmentedAsync(TestArtifactsPrefix, null).Result.Results)
                {
                    testContainer.DeleteAsync().Wait();
                }

                CloudQueueClient queueClient = StorageAccount.CreateCloudQueueClient();
                foreach (var testQueue in queueClient.ListQueuesSegmentedAsync(TestArtifactsPrefix, null).Result.Results)
                {
                    testQueue.DeleteAsync().Wait();
                }
            }
        }

        private class TestExceptionHandler : IWebJobsExceptionHandler
        {
            public ICollection<ExceptionDispatchInfo> UnhandledExceptionInfos { get; private set; }
            public ICollection<ExceptionDispatchInfo> TimeoutExceptionInfos { get; private set; }

            public void Initialize(JobHost host)
            {
                UnhandledExceptionInfos = new List<ExceptionDispatchInfo>();
                TimeoutExceptionInfos = new List<ExceptionDispatchInfo>();
            }

            public Task OnTimeoutExceptionAsync(ExceptionDispatchInfo exceptionInfo, TimeSpan timeoutGracePeriod)
            {
                TimeoutExceptionInfos.Add(exceptionInfo);
                return Task.FromResult(0);
            }

            public Task OnUnhandledExceptionAsync(ExceptionDispatchInfo exceptionInfo)
            {
                // TODO: FACAVAL - Validate this, tests are stepping over each other.
                if (!(exceptionInfo.SourceException is StorageException storageException &&
                    storageException?.InnerException is TaskCanceledException))
                {
                    UnhandledExceptionInfos.Add(exceptionInfo);
                }
                return Task.FromResult(0);
            }
        }

        private class TestFunctionEventCollector : IAsyncCollector<FunctionInstanceLogEntry>
        {
            private List<FunctionInstanceLogEntry> LogEntries { get; } = new List<FunctionInstanceLogEntry>();

            public Dictionary<Guid, StringBuilder> _state = new Dictionary<Guid, StringBuilder>();

            public Task AddAsync(FunctionInstanceLogEntry item, CancellationToken cancellationToken = default(CancellationToken))
            {
                StringBuilder prevState;
                if (!_state.TryGetValue(item.FunctionInstanceId, out prevState))
                {
                    prevState = new StringBuilder();
                    _state[item.FunctionInstanceId] = prevState;
                }
                if (item.IsStart)
                {
                    prevState.Append("[start]");
                }
                if (item.IsPostBind)
                {
                    prevState.Append("[postbind]");
                }
                if (item.IsCompleted)
                {
                    prevState.Append("[complete]");
                }            
                
                LogEntries.Add(item);
                return Task.CompletedTask;
            }

            public Task FlushAsync(CancellationToken cancellationToken = default(CancellationToken))
            {
                return Task.CompletedTask;
            }

            public int FunctionCount
            {
                get { return _state.Count; }
            }

            public void AssertFunctionCount(int expected)
            {
                // Verify the event ordering and that we got all notifications. 
                foreach (var kv in _state)
                {
                    Assert.Equal("[start][postbind][complete]", kv.Value.ToString());
                }

                var actual = this._state.Count;                
                Assert.True(actual == expected, "Actual function invocations:" + Environment.NewLine + string.Join(Environment.NewLine, this.LogEntries.Select(l => l.FunctionName)));
            }
        }
    }
}
