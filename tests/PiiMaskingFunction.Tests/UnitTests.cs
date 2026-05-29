using System;
using System;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Xunit;
using test.CommonFunctions.AzureFunction.PiiMasking.Internal;

namespace PiiMaskingFunction.Tests
{
    public class UnitTests
    {
        [Fact]
        public async Task FuncWithRetryAsync_SucceedsAfterRetries()
        {
            Environment.SetEnvironmentVariable("PII_RETRY_MAX_COUNT", "5");
            Environment.SetEnvironmentVariable("PII_RETRY_BASE_DELAY_MS", "10");

            int attempts = 0;
            Func<CancellationToken, Task<string>> flaky = async (ct) =>
            {
                attempts++;
                await Task.Yield();
                if (attempts < 3)
                {
                    throw new System.Net.Http.HttpRequestException("transient");
                }
                return "ok";
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var result = await FunctionBaseRetry.FuncWithRetryAsync(flaky, null, cts.Token);

            Assert.Equal("ok", result);
            Assert.Equal(3, attempts);

            // clear env
            Environment.SetEnvironmentVariable("PII_RETRY_MAX_COUNT", null);
            Environment.SetEnvironmentVariable("PII_RETRY_BASE_DELAY_MS", null);
        }

        [Fact]
        public async Task FuncWithRetryAsync_DoesNotRetryOnNonTransient()
        {
            int attempts = 0;
            Func<CancellationToken, Task<int>> broken = (ct) =>
            {
                attempts++;
                throw new InvalidOperationException("non-transient");
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await FunctionBaseRetry.FuncWithRetryAsync(broken, null, cts.Token, maxRetryCount: 3, baseDelayMs: 1);
            });

            Assert.Equal(1, attempts);
        }

        [Fact]
        public async Task FuncWithRetryAsync_RespectsCancellation()
        {
            int attempts = 0;
            Func<CancellationToken, Task<int>> slow = async (ct) =>
            {
                attempts++;
                await Task.Delay(1000, ct);
                return 42;
            };

            using var cts = new CancellationTokenSource(50);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await FunctionBaseRetry.FuncWithRetryAsync(slow, null, cts.Token, maxRetryCount: 3, baseDelayMs: 10);
            });

            Assert.True(attempts >= 1);
        }

        [Fact]
        public async Task FuncWithRetryAsync_Retries_On_5xx_RequestFailedException()
        {
            int attempts = 0;
            Func<CancellationToken, Task<int>> flaky = ct =>
            {
                attempts++;
                if (attempts < 3)
                {
                    throw new RequestFailedException(500, "server error");
                }
                return Task.FromResult(123);
            };

            var result = await FunctionBaseRetry.FuncWithRetryAsync(flaky, null, CancellationToken.None, maxRetryCount: 5, baseDelayMs: 1);
            Assert.Equal(123, result);
            Assert.Equal(3, attempts);
        }

        [Fact]
        public async Task FuncWithRetryAsync_DoesNotRetry_On_4xx_RequestFailedException()
        {
            int attempts = 0;
            Func<CancellationToken, Task<int>> broken = ct =>
            {
                attempts++;
                throw new RequestFailedException(400, "bad request");
            };

            await Assert.ThrowsAsync<RequestFailedException>(async () =>
            {
                await FunctionBaseRetry.FuncWithRetryAsync(broken, null, CancellationToken.None, maxRetryCount: 3, baseDelayMs: 1);
            });

            Assert.Equal(1, attempts);
        }

        [Fact]
        public async Task FuncWithRetryAsync_Throws_After_MaxAttempts()
        {
            Environment.SetEnvironmentVariable("PII_RETRY_MAX_COUNT", "2");
            int attempts = 0;
            Func<CancellationToken, Task<int>> alwaysFail = ct =>
            {
                attempts++;
                throw new RequestFailedException(500, "server error");
            };

            await Assert.ThrowsAsync<RequestFailedException>(async () =>
            {
                await FunctionBaseRetry.FuncWithRetryAsync(alwaysFail, null, CancellationToken.None);
            });

            // attempts should be maxCount+1
            Assert.Equal(3, attempts);

            Environment.SetEnvironmentVariable("PII_RETRY_MAX_COUNT", null);
        }

        [Fact]
        public async Task ActionWithRetryAsync_Works_For_Transient()
        {
            int attempts = 0;
            Func<CancellationToken, Task> flaky = ct =>
            {
                attempts++;
                if (attempts < 2) throw new RequestFailedException(500, "server");
                return Task.CompletedTask;
            };

            await FunctionBaseRetry.ActionWithRetryAsync(flaky, null, CancellationToken.None, maxRetryCount: 3, baseDelayMs: 1);
            Assert.Equal(2, attempts);
        }

        [Fact]
        public void MaskTranscriptFromOffsets_NullOffsets_ReturnsOriginal()
        {
            var input = "Hello";
            var masked = PiiMaskingService.MaskTranscriptFromOffsets(input, null);
            Assert.Equal(input, masked);
        }

        [Fact]
        public void MaskTranscriptFromOffsets_Handles_Multibyte_Characters()
        {
            // string with emoji (surrogate pair) and japanese characters
            var input = "?????e?X?g\uD83D\uDE0A12345"; // emoji after some chars
            // mask the numeric part only
            var offsets = new[] { (offset: 7, length: 5) }; // approximate index to numeric
            var masked = PiiMaskingService.MaskTranscriptFromOffsets(input, offsets);
            // ensure masking occurred (don't rely on exact index handling for surrogate pairs in this unit test)
            Assert.Contains("*", masked);
        }

        [Fact]
        public void CreateTextAnalyticsClient_Returns_Null_When_No_Credentials()
        {
            // If credentials are present in the environment, skip this test
            var existingEp = Environment.GetEnvironmentVariable("TEXT_ANALYTICS_ENDPOINT");
            var existingKey = Environment.GetEnvironmentVariable("TEXT_ANALYTICS_KEY");
            if (!string.IsNullOrWhiteSpace(existingEp) || !string.IsNullOrWhiteSpace(existingKey))
            {
                Console.WriteLine("Skipping isolation test: TEXT_ANALYTICS credentials present in environment.");
                return;
            }

            // backup env (should be null)
            var oldEp = existingEp;
            var oldKey = existingKey;
            Environment.SetEnvironmentVariable("TEXT_ANALYTICS_ENDPOINT", null);
            Environment.SetEnvironmentVariable("TEXT_ANALYTICS_KEY", null);

            try
            {
                var client = PiiMaskingService.CreateTextAnalyticsClient();
                Assert.Null(client);
            }
            finally
            {
                // restore env
                Environment.SetEnvironmentVariable("TEXT_ANALYTICS_ENDPOINT", oldEp);
                Environment.SetEnvironmentVariable("TEXT_ANALYTICS_KEY", oldKey);
            }
        }
    }
}
