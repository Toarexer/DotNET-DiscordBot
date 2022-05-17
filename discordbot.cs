using Discord;
using Discord.WebSocket;
using System.Diagnostics;
using System.Net;

namespace DiscordBot
{
    class Program
    {
        const string ChannelName = "cs-corner";
        const string TempFolder = ".cstemp/";
        const string LogFile = "discordbot.log";
        const int LogFileMaxLines = 1000;

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

                    Process.Start("docker", "build -qt dotnet_container . ").WaitForExit();

                    Docker.Refresh();
                    Docker.Start();
                    Docker.BeginOutputReadLine();
                    Docker.BeginErrorReadLine();
                    Print($"Process started (PID: {Docker.Id})");

                    Docker.WaitForExit(30000);
                    Docker.CancelOutputRead();
                    Docker.CancelErrorRead();

                    bool killed = false;
                    while (!Docker.HasExited)
                    {
                        Process.Start("kill", "-SIGKILL " + Docker.Id).WaitForExit();
                        Print("Killed process " + Docker.Id, MessageType.Warning);
                        killed = true;
                    }
                    Print($"Process exited (PID: {Docker.Id} Exitcode: {Docker.ExitCode})");

                    string result = $"process exited with code `{Docker.ExitCode}`{(killed ? " (killed)" : string.Empty)}";
                    Docker.Close();

                    CsChannel?.SendMessageAsync(result).Wait();
                    Print("Sent response: " + result);
                    foreach (string file in Directory.EnumerateFiles(TempFolder))
                        File.Delete(file);
                }
                catch (Exception exception)
                {
                    Print("Exception thrown:\n" + exception, MessageType.Error);
                    Docker.CancelOutputRead();
                    Docker.CancelErrorRead();
                    Docker.Close();
                    foreach (string file in Directory.EnumerateFiles(TempFolder))
                        File.Delete(file);
                }
        }

        enum MessageType
        {
            Info, Warning, Error
        }

        static void Print(string message, MessageType type = MessageType.Info)
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

        Task Log(LogMessage msg)
        {
            Console.WriteLine(msg);
            return Task.CompletedTask;
        }

        static ISocketMessageChannel? CsChannel;

        async Task MainAsync()
        {
            DiscordSocketClient Client = new();
            Client.Log += Log;
            Client.MessageReceived += Respond;
            Client.MessageReceived += (SocketMessage socketmsg) =>
            {
                if (socketmsg.Channel.Name == ChannelName)
                    CsChannel = socketmsg.Channel;
                return Task.CompletedTask;
            };

            if (!Directory.Exists(TempFolder))
                Directory.CreateDirectory(TempFolder);
            ProcessThread.Start();

            await Client.LoginAsync(TokenType.Bot, File.ReadAllText("token"));
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
                Print("Recieved input: " + socketmsg.Content);
                await Docker.StandardInput.WriteLineAsync(socketmsg.Content);
                return;
            }

            if (socketmsg is SocketUserMessage msg)
            {
                if (!string.IsNullOrEmpty(code = IsMessageCode(msg.Content)))
                {
                    Print("Recieved message: " + msg.Content);
                    await DeleteMessages(msg.Channel, 1);
                    await File.WriteAllTextAsync(TempFolder + "Program.cs", code);
                    Start = true;
                    return;
                }

                if (GetCSFiles(msg.Attachments))
                {
                    Print("Recieved message: " + msg.Content);
                    await DeleteMessages(msg.Channel, 1);
                    Start = true;
                    return;
                }
            }
        }

        async Task DeleteMessages(ISocketMessageChannel channel, int skip)
        {
            var messages = await channel.GetMessagesAsync().FlattenAsync();
            foreach (IMessage m in messages.Skip(skip))
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
            bool found = false;
#pragma warning disable SYSLIB0014
            using (WebClient client = new())
#pragma warning restore SYSLIB0014
                foreach (Attachment item in attachments)
                {
                    Print($"Recieved attachments: {item.Filename} {item.Url}");
                    if (item.Filename.ToLower().EndsWith(".cs"))
                    {
                        client.DownloadFile(item.Url, TempFolder + item.Filename);
                        found = true;
                    }
                }
            return found;
        }

        static void Main(string[] args) => new Program().MainAsync().GetAwaiter().GetResult();
    }
}
