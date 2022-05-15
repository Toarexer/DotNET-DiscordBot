using Discord;
using Discord.WebSocket;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace DiscordBot
{
    class Program
    {
        const string ChannelName = "cs-corner";
        const string TempFile = ".cstemp";
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
            Regex regex = new(@".\[.+\=", RegexOptions.Multiline | RegexOptions.Compiled);

            Docker.OutputDataReceived += new DataReceivedEventHandler((object sender, DataReceivedEventArgs e) =>
            {
                if (e.Data is not null)
                    CsChannel?.SendMessageAsync($"> {e.Data}");
            });
            Docker.ErrorDataReceived += new DataReceivedEventHandler((object sender, DataReceivedEventArgs e) =>
            {
                if (e.Data is not null)
                    CsChannel?.SendMessageAsync($"> *{e.Data}*");
            });

            while (true)
                try
                {
                    while (!Start)
                        Thread.Sleep(0);
                    Start = false;

                    Process.Start("docker", "build -t dotnet_container .").WaitForExit();

                    Docker.Refresh();
                    Docker.Start();
                    Docker.BeginOutputReadLine();
                    Docker.BeginErrorReadLine();
                    Print($"Process started (PID: {Docker.Id})");

                    Docker.WaitForExit(30000);
                    bool killed = false;
                    while (!Docker.HasExited)
                    {
                        Process.Start("kill", "-SIGKILL " + Docker.Id).WaitForExit();
                        Print("Killed process " + Docker.Id, MessageType.Warning);
                        killed = true;
                    }
                    Print($"Process exited (PID: {Docker.Id} Exitcode: {Docker.ExitCode})");

                    string result = $"process exited with code `{Docker.ExitCode}`{(killed ? " (killed)" : string.Empty)}";                    
                    Docker.CancelOutputRead();
                    Docker.CancelErrorRead();
                    Docker.Close();

                    Print("Sent response: " + result);
                    CsChannel?.SendMessageAsync();
                }
                catch (Exception exception)
                {
                    Print("Exception thrown:\n" + exception, MessageType.Warning);
                    Docker.CancelOutputRead();
                    Docker.CancelErrorRead();
                    Docker.Close();
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

            ProcessThread.Start();

            await Client.LoginAsync(TokenType.Bot, File.ReadAllText("token"));
            await Client.StartAsync();
            await Task.Delay(-1);
        }

        async Task Respond(SocketMessage socketmsg)
        {
            if (socketmsg.Channel.Name == ChannelName && !socketmsg.Author.IsBot)
            {
                if (socketmsg.Content == "!clear")
                {
                    var messages = await socketmsg.Channel.GetMessagesAsync().FlattenAsync();
                    foreach (IMessage m in messages)
                        await socketmsg.Channel.DeleteMessageAsync(m);
                }
                else
                {
                    string code;

                    if (!Process.GetProcesses().Contains(Docker))
                    {
                        if (socketmsg is SocketUserMessage msg && IsMessageCode(msg.Content, out code))
                        {
                            Print("Recieved message: " + msg.Content);
                            var messages = await msg.Channel.GetMessagesAsync().FlattenAsync();
                            foreach (IMessage m in messages.Skip(1))
                                await msg.Channel.DeleteMessageAsync(m);

                            await File.WriteAllTextAsync(TempFile, code);
                            Start = true;
                        }
                        else
                        {
                            Print("Recieved input: " + socketmsg.Content);
                            await Docker.StandardInput.WriteLineAsync(socketmsg.Content);
                        }
                    }
                    else
                        await socketmsg.Channel.DeleteMessageAsync(socketmsg);
                }
            }
        }

        bool IsMessageCode(string message, out string code)
        {
            if (message.StartsWith("```cs") && message.EndsWith("```"))
            {
                code = message.Substring(5, message.Length - 8);
                return true;
            }
            code = string.Empty;
            return false;
        }

        static void Main(string[] args) => new Program().MainAsync().GetAwaiter().GetResult();
    }
}
