using Discord;
using Discord.WebSocket;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;

namespace DiscordBot
{
    class Program
    {
        static string Token => File.ReadAllText("token");

        const string ChannelName = "cs-corner";  // Name of the discord text channel
        const string TempFolder = ".cstemp/";    // The temp folder where the files can be stored
        const string LogFile = "discordbot.log"; // The log file
        const int LogFileMaxLines = 1000;        // How many line should the log file have
        const int MaxExecutionTime = 30000;      // Maximum time given to the docker process to finish before killing it in miliseconds

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

        static bool Start = false;
        static Thread ProcessThread = new(ProcessThreadFunc);

        static void ProcessThreadFunc()
        {
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

            while (true)
                try
                {
                    while (!Start)
                        Thread.Sleep(0);
                    Start = false;

                    Process.Start("docker", "build -qt dotnet_container .").WaitForExit();

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
                        Process.Start("kill", "-SIGKILL " + Docker.Id).WaitForExit();
                        Log("Killed process " + Docker.Id, MessageType.Warning);
                        killed = true;
                    }
                    Log($"Process exited (PID: {Docker.Id} Exitcode: {Docker.ExitCode})");

                    string result = $"process exited with code `{Docker.ExitCode}`{(killed ? " (killed)" : string.Empty)}";
                    Docker.Close();

                    CsChannel?.SendMessageAsync(result).Wait();
                    Log("Sent response: " + result);
                    ClearDir(TempFolder);
                }
                catch (Exception exception)
                {
                    Log("Exception thrown:\n" + exception, MessageType.Error);
                    Docker.CancelOutputRead();
                    Docker.CancelErrorRead();
                    Docker.Close();
                    ClearDir(TempFolder);
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

        static ISocketMessageChannel? CsChannel;

        async Task MainAsync()
        {
            string token = Token;
            if (string.IsNullOrEmpty(token))
            {
                Console.Error.WriteLine("You must provide a discord token!");
                Environment.Exit(1);
            }

            DiscordSocketClient Client = new();
            Client.Log += (LogMessage msg) =>
            {
                Console.WriteLine(msg);
                return Task.CompletedTask;
            };
            Client.MessageReceived += (SocketMessage socketmsg) =>
            {
                if (socketmsg.Channel.Name == ChannelName)
                    CsChannel = socketmsg.Channel;
                return Task.CompletedTask;
            };
            Client.MessageReceived += Respond;

            if (!Directory.Exists(TempFolder))
                Directory.CreateDirectory(TempFolder);
            ProcessThread.Start();

            await Client.LoginAsync(TokenType.Bot, token);
            await Client.StartAsync();
            await Task.Delay(-1);
        }

        async Task Respond(SocketMessage socketmsg)
        {
            if (socketmsg.Channel.Name != ChannelName || socketmsg.Author.IsBot)
                return;

            if (socketmsg.Content == "!clear")
            {
                await DeleteMessages(socketmsg.Channel, 0);
                return;
            }

            string? code;
            if (Process.GetProcesses().Contains(Docker))
            {
                Log("Recieved input: " + socketmsg.Content);
                await Docker.StandardInput.WriteLineAsync(socketmsg.Content);
                return;
            }

            if (socketmsg is SocketUserMessage msg)
            {
                if (!string.IsNullOrEmpty(code = IsMessageCode(msg.Content)))
                {
                    Log("Recieved message: " + msg.Content);
                    await DeleteMessages(msg.Channel, 1);
                    await File.WriteAllTextAsync(TempFolder + "Program.cs", code);
                    Start = true;
                    return;
                }

                if (GetCSFiles(msg.Attachments))
                {
                    Log("Recieved message: " + msg.Content);
                    await DeleteMessages(msg.Channel, 1);
                    Start = true;
                    return;
                }
            }
        }

        async Task DeleteMessages(ISocketMessageChannel channel, int skip)
        {
            var messages = await channel.GetMessagesAsync().FlattenAsync();
            foreach (IMessage m in messages.Skip(skip).Where(x => !x.IsPinned))
                await channel.DeleteMessageAsync(m);
        }

        string? IsMessageCode(string message)
        {
            if (message.Length > 8 && message.Substring(0, 5).ToLower() == "```cs" && message.EndsWith("```"))
                return message.Substring(5, message.Length - 8);
            return null;
        }

        bool GetCSFiles(IReadOnlyCollection<Attachment> attachments)
        {
            bool found = attachments.Any(x => x.Filename.ToLower().EndsWith(".cs"));
            if (!(GetZipFiles(attachments) || found))
            {
                ClearDir(TempFolder);
                return false;
            }

#pragma warning disable SYSLIB0014
            using (WebClient client = new())
#pragma warning restore SYSLIB0014
                foreach (Attachment item in attachments.Where(x => !x.Filename.ToLower().EndsWith(".zip")))
                {
                    client.DownloadFile(item.Url, TempFolder + item.Filename);
                    Log($"Downloaded: " + item.Url);
                }
            return true;
        }

        bool GetZipFiles(IReadOnlyCollection<Attachment> attachments)
        {
            bool found = false;

#pragma warning disable SYSLIB0014
            using (WebClient client = new())
#pragma warning restore SYSLIB0014
                foreach (Attachment item in attachments.Where(x => x.Filename.ToLower().EndsWith(".zip")))
                {
                    client.DownloadFile(item.Url, TempFolder + item.Filename);
                    Log($"Downloaded: " + item.Url);
                    foreach (ZipArchiveEntry entry in ZipFile.OpenRead(TempFolder + item.Filename).Entries)
                    {
                        if (entry.ExternalAttributes == 0x41C00010) // 0x41C00010 if it is a directory?
                            Directory.CreateDirectory(TempFolder + entry.FullName);
                        else
                            entry.ExtractToFile(TempFolder + entry.FullName, true);
                        Log($"Extracted: " + entry.FullName);
                        if (entry.Name.ToLower().EndsWith(".cs"))
                            found = true;
                    }
                }
            return found;
        }

        static void Main(string[] args) => new Program().MainAsync().GetAwaiter().GetResult();
    }
}
