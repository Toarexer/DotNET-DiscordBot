using Discord;
using Discord.WebSocket;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;

namespace DiscordBot
{
    static class Program
    {
        const string TempDir = ".temp/";                    // The temp directory
        const string ChannelsFile = "discordbot.channels";  // The file that stores the channel ids the bot will listen on
        const string LogFile = "discordbot.log";            // The log file
        const int LogFileMaxLines = 1000;                   // How many line should the log file have
        const int MaxExecutionTime = 30000;                 // Maximum time given to the docker process to finish before killing it in miliseconds

        static bool keeptemp = false;
        static bool keepmessages = false;

        class Docker
        {
            public enum ProcessExitCause
            {
                Ok, Interrupted, TimedOut
            }

            public ISocketMessageChannel DiscordChannel;
            public Process DockerProcess;
            public Task DokcerTask;
            public ProcessExitCause ExitCause = ProcessExitCause.Ok;

            public Docker(ISocketMessageChannel channel)
            {
                DiscordChannel = channel;

                DockerProcess = new()
                {
                    StartInfo = new("docker", "run -i dotnet_container")
                    {
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    }
                };
                DockerProcess.OutputDataReceived += new DataReceivedEventHandler((object sender, DataReceivedEventArgs e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                        DiscordChannel.SendMessageAsync($"> {e.Data}").Wait();
                });
                DockerProcess.ErrorDataReceived += new DataReceivedEventHandler((object sender, DataReceivedEventArgs e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                        DiscordChannel.SendMessageAsync($"> *{e.Data}*").Wait();
                });

                DokcerTask = Task.Run(StartDocker);
            }

            void StartDocker()
            {
                try
                {
                    Process.Start("docker", $"build --build-arg TARGETDIR={DiscordChannel.Id} -qt dotnet_container .").WaitForExit();

                    DockerProcess.Start();
                    DockerProcess.BeginOutputReadLine();
                    DockerProcess.BeginErrorReadLine();
                    Log($"Process started (PID: {DockerProcess.Id})");

                    DockerProcess.WaitForExit(MaxExecutionTime);
                    DockerProcess.CancelOutputRead();
                    DockerProcess.CancelErrorRead();

                    if (!DockerProcess.HasExited)
                        Kill(false);
                    Log($"Process exited (PID: {DockerProcess.Id} Exitcode: {DockerProcess.ExitCode})");

                    string result = $"process exited with code `{DockerProcess.ExitCode}`" + ExitCause switch
                    {
                        ProcessExitCause.Interrupted => " (interrupted)",
                        ProcessExitCause.TimedOut => " (killed)",
                        _ => string.Empty
                    };
                    DiscordChannel.SendMessageAsync(result).Wait();
                    Log($"Process result (PID: {DockerProcess.Id} Channel: {DiscordChannel.Id} Result: \"{result}\")");
                }
                catch (Exception exception)
                {
                    DiscordChannel.SendMessageAsync("*An error occurred while running the code!*").Wait();
                    Log($"Exception thrown (PID: {DockerProcess.Id} Exception: {exception})", MessageType.Error);
                }

                DockerProcess.Close();
                Containers.Remove(this);
                DeleteTempDir(DiscordChannel.Id);
            }

            public void Kill(bool interrupted)
            {
                if (interrupted)
                {
                    Process.Start("kill", "-SIGINT " + DockerProcess.Id).WaitForExit();
                    Log($"Process interrupted (PID: {DockerProcess.Id})");
                    ExitCause = ProcessExitCause.Interrupted;
                }
                else
                {
                    DockerProcess.Kill(true);
                    Log($"Process timed out (PID: {DockerProcess.Id})", MessageType.Warning);
                    ExitCause = ProcessExitCause.TimedOut;
                }
            }

            public static List<Docker> Containers = new();
        }

        enum MessageType
        {
            Info, Warning, Error
        }

        static void Log(string message, MessageType type = MessageType.Info)
        {
            Console.CursorLeft = 0;
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write("[{0:00}:{1:00}:{2:00}] ", DateTime.Now.Hour, DateTime.Now.Minute, DateTime.Now.Second);
            Console.ForegroundColor = type switch
            {
                MessageType.Error => ConsoleColor.Red,
                MessageType.Warning => ConsoleColor.Yellow,
                _ => ConsoleColor.Blue,
            };
            Console.Write("! ");
            Console.ResetColor();
            Console.WriteLine(message);

            char c = type switch
            {
                MessageType.Error => '*',
                MessageType.Warning => '!',
                _ => ' ',
            };
            File.AppendAllText(LogFile, $"[{DateTime.Now.Hour:00}:{DateTime.Now.Minute:00}:{DateTime.Now.Second:00}] <{c}> {message}\n");

            string[] lines = File.ReadAllLines(LogFile);
            if (lines.Length > LogFileMaxLines)
                File.WriteAllLines(LogFile, lines.Skip(lines.Length - LogFileMaxLines));
        }

        static void DeleteTempDir(ulong channelID)
        {
            if (!keeptemp)
                Directory.Delete(TempDir + channelID, true);
        }

        static bool IsChannelOk(ulong id)
        {
            ulong num;
            foreach (string s in File.ReadAllLines(ChannelsFile))
                if (ulong.TryParse(s.Substring(0, 18), out num) && num == id)
                    return true;
            return false;
        }

        static async void DeleteMessages(ISocketMessageChannel channel, int skip = 0)
        {
            var messages = await channel.GetMessagesAsync().FlattenAsync();
            foreach (IMessage m in messages.Skip(skip).Where(x => !x.IsPinned))
                _ = channel.DeleteMessageAsync(m);
        }

        static bool GetCodeFromMessage(SocketUserMessage msg)
        {
            if (msg.Content.Length > 8 && msg.Content.Substring(0, 5).ToLower() == "```cs" && msg.Content.EndsWith("```"))
            {
                Log("Recieved message: " + msg);
                string file = $"{TempDir}{msg.Channel.Id}/{msg.Channel.Id}.cs";
                File.WriteAllText(file, msg.Content.Substring(5, msg.Content.Length - 8));
                return true;
            }
            return false;
        }

#pragma warning disable SYSLIB0014
        static bool GetCSFiles(SocketUserMessage msg)
        {
            IReadOnlyCollection<Attachment> attachments = msg.Attachments;
            if (!(GetZipFiles(msg) | attachments.Any(x => x.Filename.ToLower().EndsWith(".cs"))))
                return false;
            List<Task> tasks = new();

            using (WebClient client = new())
                foreach (Attachment item in attachments.Where(x => !x.Filename.ToLower().EndsWith(".zip")))
                    tasks.Add(Task.Run(async () =>
                    {
                        await client.DownloadFileTaskAsync(item.Url, $"{TempDir}{msg.Channel.Id}/{item.Filename}");
                        Log($"Downloaded: " + item.Url);
                    }));
            Task.WhenAll(tasks).Wait();
            return true;
        }

        static bool GetZipFiles(SocketUserMessage msg)
        {
            IReadOnlyCollection<Attachment> attachments = msg.Attachments;
            string tempdir = TempDir + msg.Channel.Id + '/';
            bool found = false;
            List<Task> tasks = new();

            using (WebClient client = new())
                foreach (Attachment item in attachments.Where(x => x.Filename.ToLower().EndsWith(".zip")))
                    tasks.Add(Task.Run(async () =>
                    {
                        await client.DownloadFileTaskAsync(item.Url, tempdir + item.Filename);
                        Log($"Downloaded: " + item.Url);
                        foreach (ZipArchiveEntry entry in ZipFile.OpenRead(tempdir + item.Filename).Entries)
                            if (entry.ExternalAttributes == 0x41C00010) // 0x41C00010 if it is a directory?
                            {
                                Directory.CreateDirectory(tempdir + entry.FullName);
                                Log($"Created directory: " + entry.FullName);
                            }
                            else
                            {
                                entry.ExtractToFile(tempdir + entry.FullName, true);
                                Log($"Extracted: " + entry.FullName);
                                if (entry.Name.ToLower().EndsWith(".cs"))
                                    found = true;
                            }
                    }));
            Task.WhenAll(tasks).Wait();
            return found;
        }
#pragma warning restore SYSLIB0014

        static Task Recieved(SocketMessage socketmsg)
        {
            if (socketmsg is SocketUserMessage msg && IsChannelOk(msg.Channel.Id) && !msg.Author.IsBot)
            {
                if (msg.Content == "!clear")
                    DeleteMessages(msg.Channel, 0);
                else if (Docker.Containers.Any(x => x.DiscordChannel.Id == msg.Channel.Id))
                {
                    Docker docker = Docker.Containers.First(x => x.DiscordChannel.Id == msg.Channel.Id);
                    if (msg.Content == "^c" || msg.Content == "^C")
                        docker.Kill(true);
                    Log("Recieved input: " + msg.Content);
                    docker.DockerProcess.StandardInput.WriteLineAsync(msg.Content);
                }
                else
                {
                    string tempdir = TempDir + msg.Channel.Id;
                    Directory.CreateDirectory(tempdir);
                    if (GetCodeFromMessage(msg) | GetCSFiles(msg))
                    {
                        DeleteMessages(msg.Channel, 1);
                        Docker.Containers.Add(new(msg.Channel));
                    }
                    else
                        DeleteTempDir(msg.Channel.Id);
                }
            }
            return Task.CompletedTask;
        }

        static async Task MainAsync(string[] args)
        {
            if (!Directory.Exists(TempDir))
                Directory.CreateDirectory(TempDir);

            if (args.Contains("cleartemp"))
            {
                foreach (string dir in Directory.EnumerateDirectories(TempDir))
                    Directory.Delete(dir, true);
                Environment.Exit(0);
            }

            // Discord bot token
            string token = args.Contains("token=") ? args.Last(x => x.StartsWith("token=")).Substring(6) : File.ReadAllText("discordbot.token");
            if (string.IsNullOrEmpty(token))
            {
                Console.Error.WriteLine("You must provide a discord token!");
                Environment.Exit(1);
            }

            keeptemp = args.Contains("keeptemp");
            keepmessages = args.Contains("keepmessages");

            if (!File.Exists(ChannelsFile))
                File.WriteAllText(ChannelsFile, "000000000000000000 <- Replace the numbers with the discord text channel id!");

            Console.CancelKeyPress += (object? sender, ConsoleCancelEventArgs e) =>
            {
                Console.CursorLeft = 0;
                Console.WriteLine("exiting...");
                Environment.Exit(0);
            };

            DiscordSocketClient client = new();
            client.MessageReceived += Recieved;
            client.Log += (LogMessage msg) =>
            {
                Console.WriteLine(msg);
                return Task.CompletedTask;
            };

            await client.LoginAsync(TokenType.Bot, token);
            await client.StartAsync();
            await Task.Delay(-1);
        }

        static void Main(string[] args) => MainAsync(args).GetAwaiter().GetResult();
    }
}
