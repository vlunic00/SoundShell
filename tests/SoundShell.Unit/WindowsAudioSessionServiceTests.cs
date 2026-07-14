using System;
using Microsoft.Extensions.Logging.Abstractions;
using SoundShell.Audio;
using Xunit;

namespace SoundShell.Unit
{
    public class WindowsAudioSessionServiceTests
    {
        [Theory]
        [InlineData(-0.01f)]
        [InlineData(1.01f)]
        public void SetSessionVolume_Rejects_OutOfRange_Values(float volume)
        {
            using var service = new WindowsAudioSessionService(NullLogger<WindowsAudioSessionService>.Instance);

            Assert.Throws<ArgumentOutOfRangeException>(() => service.SetSessionVolume("session", volume));
        }

        [Fact]
        public void StartMonitoring_Throws_After_Dispose()
        {
            var service = new WindowsAudioSessionService(NullLogger<WindowsAudioSessionService>.Instance);
            service.Dispose();

            Assert.Throws<ObjectDisposedException>(() => service.StartMonitoring());
        }
    }
}
