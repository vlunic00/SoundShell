using System;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using SoundShell.Audio;
using Xunit;

namespace SoundShell.Unit
{
    public class WindowsAudioSessionServiceTests
    {
        private class TransientSessionManager
        {
            public int Calls;
            public void RegisterSessionNotification(object sink)
            {
                Calls++;
                if (Calls < 3)
                    throw new InvalidOperationException("transient");
                // succeed on 3rd attempt
            }
        }

        [Fact]
        public void TryRegisterSessionNotificationWithRetry_SucceedsAfterTransientFailures()
        {
            var logger = NullLogger<WindowsAudioSessionService>.Instance;
            var service = new WindowsAudioSessionService(logger);

            var method = typeof(WindowsAudioSessionService).GetMethod("TryRegisterSessionNotificationWithRetry", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);

            var manager = new TransientSessionManager();
            var args = new object[] { manager, null, null };

            var result = (bool)method.Invoke(service, args);

            // out parameter is at index 2
            var lastException = args[2] as Exception;

            Assert.True(result, "Expected registration to eventually succeed");
            Assert.Null(lastException);
            Assert.Equal(3, manager.Calls);
        }
    }
}
