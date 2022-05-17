# .NET Discord Bot

## Info
The bot awaits messages and files on the specified text channel.
When the bot recieves C# code it runs it in a docker container and redirects the process' standard in, out and error streams.\
The output stream is read line by line and sent back through the discord chat.\
If we send any messages in the chat while the process is already running the message will be sent to it's standard in.\
The process is given 30 seconds to run before it gets killed.

> The `OutputDataReceived` event may not recieve all data before the process exits.
> We may have to use `Thread.Sleep` or other means at the end of the code to make the process wait a bit before exiting.

## Running code
There are two ways to run C# code.\
The first option is to simply send code in a cs code block.
The second one is to send the .cs files in a message. The message can contain multiple files to be compiled.\
This allow to send larger codes, but it takes longer because each file must be downloaded.

* Example of a C# code block\
` ```cs`<br>
`Console.WriteLine("Hello There!");`<br>
` ``` `