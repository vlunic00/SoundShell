using System;
using System.Globalization;
using SoundShell.Audio;

namespace SoundShell.PoC
{
    class Program
    {
        static void Main(string[] args)
        {
            try
            {
                using var audioService = new WindowsAudioSessionService();
                if (args.Length == 0)
                {
                    ListSessions(audioService);
                    PrintUsage();
                    return;
                }

                var command = args[0].ToLowerInvariant();
                switch (command)
                {
                    case "list":
                        ListSessions(audioService);
                        break;
                    case "set-volume":
                        RunSetVolume(audioService, args);
                        break;
                    case "mute":
                        RunMute(audioService, args, true);
                        break;
                    case "unmute":
                        RunMute(audioService, args, false);
                        break;
                    default:
                        Console.WriteLine($"Unknown command: {command}");
                        PrintUsage();
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("ERROR: " + ex.Message);
            }
        }

        private static void ListSessions(IAudioSessionService audioService)
        {
            var sessions = audioService.GetAudioSessions();
            if (sessions.Count == 0)
            {
                Console.WriteLine("No active audio sessions were found.");
                return;
            }

            Console.WriteLine("Active audio sessions:");
            for (var i = 0; i < sessions.Count; i++)
            {
                var session = sessions[i];
                Console.WriteLine($"[{i + 1}] {session.ProcessName} (PID={session.ProcessId}) " +
                    $"DisplayName='{session.DisplayName}' Volume={session.Volume:P0} Muted={session.IsMuted} " +
                    $"SessionId={session.SessionIdentifier}");
            }
        }

        private static void RunSetVolume(IAudioSessionService audioService, string[] args)
        {
            if (args.Length != 3)
            {
                Console.WriteLine("Usage: set-volume <session-id-or-index> <0.0-1.0>");
                return;
            }

            var target = args[1];
            if (!float.TryParse(args[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var volume))
            {
                Console.WriteLine("Volume must be a number between 0.0 and 1.0.");
                return;
            }

            var sessionId = ResolveSessionIdentifier(audioService, target);
            audioService.SetSessionVolume(sessionId, volume);
            Console.WriteLine($"Updated volume for session {sessionId} to {volume:P0}");
        }

        private static void RunMute(IAudioSessionService audioService, string[] args, bool mute)
        {
            if (args.Length != 2)
            {
                Console.WriteLine(mute ? "Usage: mute <session-id-or-index>" : "Usage: unmute <session-id-or-index>");
                return;
            }

            var sessionId = ResolveSessionIdentifier(audioService, args[1]);
            audioService.SetSessionMute(sessionId, mute);
            Console.WriteLine($"Session {sessionId} {(mute ? "muted" : "unmuted")}. ");
        }

        private static string ResolveSessionIdentifier(IAudioSessionService audioService, string sessionIdOrIndex)
        {
            if (int.TryParse(sessionIdOrIndex, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
            {
                var sessions = audioService.GetAudioSessions();
                if (index < 1 || index > sessions.Count)
                    throw new ArgumentOutOfRangeException(nameof(sessionIdOrIndex), "Index is out of range.");

                return sessions[index - 1].SessionIdentifier;
            }

            return sessionIdOrIndex;
        }

        private static void PrintUsage()
        {
            Console.WriteLine();
            Console.WriteLine("Usage:");
            Console.WriteLine("  list");
            Console.WriteLine("  set-volume <session-id-or-index> <0.0-1.0>");
            Console.WriteLine("  mute <session-id-or-index>");
            Console.WriteLine("  unmute <session-id-or-index>");
            Console.WriteLine();
            Console.WriteLine("Examples:");
            Console.WriteLine("  SoundShell.PoC list");
            Console.WriteLine("  SoundShell.PoC set-volume 1 0.5");
            Console.WriteLine("  SoundShell.PoC mute {sessionId}");
        }
    }
}
