using Discord;
using Discord.WebSocket;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;

namespace DiscordBot
{
    static class Program
    {
        const string ChannelName = "cs-corner";  // Name of the discord text channel
        const string TempFolder = ".cstemp/";    // The temp folder where the files can be stored
        const string LogFile = "discordbot.log"; // The log file
        const int LogFileMaxLines = 1000;        // How many line should the log file have
        const int MaxExecutionTime = 30000;      // Maximum time given to the docker process to finish before killing it in miliseconds

        static bool keeptemp = false;

        enum MessageType
        {
            Info, Warning, Error
        }

        static ISocketMessageChannel? CsChannel;

        static Task? DockerTask;

        static Process Docker = new()
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

        static async void StartDocker()
        {
            try
            {
                await Process.Start("docker", "build -qt dotnet_container .").WaitForExitAsync();

                Docker.Refresh();
                Docker.Start();
                Docker.BeginOutputReadLine();
                Docker.BeginErrorReadLine();
                Log($"Process started (PID: {Docker.Id})");

                Docker.WaitForExit(MaxExecutionTime);
                Docker.CancelOutputRead();
                Docker.CancelErrorRead();

                bool killed = false;
                while (!Docker.HasExited)
                {
                    Docker.Kill(true);
                    Log("Killed process " + Docker.Id, MessageType.Warning);
                    killed = true;
                }
                Log($"Process exited (PID: {Docker.Id} Exitcode: {Docker.ExitCode})");

                string result = $"process exited with code `{Docker.ExitCode}`{(killed ? " (killed)" : string.Empty)}";
                Docker.Close();

                CsChannel?.SendMessageAsync(result).Wait();
                Log("Sent response: " + result);
                ClearTempDir();
            }
            catch (Exception exception)
            {
                CsChannel?.SendMessageAsync("*An error occurred while running the code!*").Wait();
                Log("Exception thrown: " + exception, MessageType.Error);

                Docker.CancelOutputRead();
                Docker.CancelErrorRead();
                Docker.Close();
                ClearTempDir();
            }
        }

        static void ClearDir(string path)
        {
            foreach (string dir in Directory.EnumerateDirectories(path))
            {
                ClearDir(dir);
                Directory.Delete(dir);
            }
            foreach (string file in Directory.EnumerateFiles(path))
                File.Delete(file);
        }

        static void ClearTempDir()
        {
            if (!keeptemp)
                ClearDir(TempFolder);
        }

        static void Log(string message, MessageType type = MessageType.Info)
        {
            message = message.Replace("\n", "\n             ");

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

        static async Task Recieved(SocketMessage socketmsg)
        {
            if (socketmsg.Channel.Name != ChannelName || socketmsg.Author.IsBot)
                return;

            if (socketmsg.Content == "!clear")
            {
                await DeleteMessages(socketmsg.Channel, 0);
                return;
            }

            string? code;
            if (DockerTask?.Status == TaskStatus.Running)
            {
                Log("Recieved input: " + socketmsg.Content);
                await Docker.StandardInput.WriteLineAsync(socketmsg.Content);
                return;
            }

            if (socketmsg is SocketUserMessage msg)
            {
                bool gotmsg;
                if (gotmsg = !string.IsNullOrEmpty(code = IsMessageCode(msg.Content)))
                {
                    Log("Recieved message: " + msg.Content);
                    await DeleteMessages(msg.Channel, 1);
                    await File.WriteAllTextAsync($"{TempFolder}{new Random().Next() | 0xFF}.cs", code);
                }

                if (GetCSFiles(msg.Attachments) || gotmsg)
                {
                    await DeleteMessages(msg.Channel, 1);
                    DockerTask = Task.Run(StartDocker);
                }
            }
        }

        static async Task DeleteMessages(ISocketMessageChannel channel, int skip = 0)
        {
            var messages = await channel.GetMessagesAsync().FlattenAsync();
            foreach (IMessage m in messages.Skip(skip).Where(x => !x.IsPinned))
                _ = channel.DeleteMessageAsync(m);
        }

        static string? IsMessageCode(string message)
        {
            if (message.Length > 8 && message.Substring(0, 5).ToLower() == "```cs" && message.EndsWith("```"))
                return message.Substring(5, message.Length - 8);
            return null;
        }

#pragma warning disable SYSLIB0014
        static bool GetCSFiles(IReadOnlyCollection<Attachment> attachments)
        {
            bool found = attachments.Any(x => x.Filename.ToLower().EndsWith(".cs"));
            if (!(GetZipFiles(attachments) || found))
            {
                ClearTempDir();
                return false;
            }
            List<Task> tasks = new();

            using (WebClient client = new())
                foreach (Attachment item in attachments.Where(x => !x.Filename.ToLower().EndsWith(".zip")))
                    tasks.Add(Task.Run(async () =>
                    {
                        await client.DownloadFileTaskAsync(item.Url, TempFolder + item.Filename);
                        Log($"Downloaded: " + item.Url);
                    }));
            Task.WhenAll(tasks).Wait();
            return true;
        }

        static bool GetZipFiles(IReadOnlyCollection<Attachment> attachments)
        {
            bool found = false;
            List<Task> tasks = new();

            using (WebClient client = new())
                foreach (Attachment item in attachments.Where(x => x.Filename.ToLower().EndsWith(".zip")))
                    tasks.Add(Task.Run(async () =>
                    {
                        await client.DownloadFileTaskAsync(item.Url, TempFolder + item.Filename);
                        Log($"Downloaded: " + item.Url);
                        foreach (ZipArchiveEntry entry in ZipFile.OpenRead(TempFolder + item.Filename).Entries)
                            if (entry.ExternalAttributes == 0x41C00010) // 0x41C00010 if it is a directory?
                            {
                                Directory.CreateDirectory(TempFolder + entry.FullName);
                                Log($"Created directory: " + entry.FullName);
                            }
                            else
                            {
                                entry.ExtractToFile(TempFolder + entry.FullName, true);
                                Log($"Extracted: " + entry.FullName);
                                if (entry.Name.ToLower().EndsWith(".cs"))
                                    found = true;
                            }
                    }));
            return found;
        }
#pragma warning restore SYSLIB0014

        static void SetEvents(DiscordSocketClient client)
        {
            client.MessageReceived += Recieved;

            client.MessageReceived += (SocketMessage socketmsg) =>
            {
                if (socketmsg.Channel.Name == ChannelName)
                    CsChannel = socketmsg.Channel;
                return Task.CompletedTask;
            };

            client.Log += (LogMessage msg) =>
            {
                Console.WriteLine(msg);
                return Task.CompletedTask;
            };

            Docker.OutputDataReceived += new DataReceivedEventHandler((object sender, DataReceivedEventArgs e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    CsChannel?.SendMessageAsync($"> {e.Data}").Wait();
            });

            Docker.ErrorDataReceived += new DataReceivedEventHandler((object sender, DataReceivedEventArgs e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    CsChannel?.SendMessageAsync($"> *{e.Data}*").Wait();
            });

            Console.CancelKeyPress += (object? sender, ConsoleCancelEventArgs e) =>
            {
                Console.CursorLeft = 0;
                Console.WriteLine("exiting...");
                Environment.Exit(0);
            };
        }

        static async Task MainAsync(string[] args)
        {
            // Discord bot token
            string token = args.Contains("token=") ? args.Last(x => x.StartsWith("token=")).Substring(6) : File.ReadAllText("token");
            if (string.IsNullOrEmpty(token))
            {
                Console.Error.WriteLine("You must provide a discord token!");
                Environment.Exit(1);
            }

            keeptemp = args.Contains("keeptemp");

            DiscordSocketClient client = new();
            SetEvents(client);

            if (!Directory.Exists(TempFolder))
                Directory.CreateDirectory(TempFolder);

            await client.LoginAsync(TokenType.Bot, token);
            await client.StartAsync();
            await Task.Delay(-1);
        }

        static void Main(string[] args) => MainAsync(args).GetAwaiter().GetResult();
    }
}
