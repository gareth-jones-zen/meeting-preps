// meeting-preps.exe: every command, with output. Subjects carry emoji, so the console gets UTF-8.
try
{
    Console.OutputEncoding = new System.Text.UTF8Encoding(false);
}
catch (IOException)
{
    // no console: output is redirected
}
return MeetingPreps.Cli.Run(args, new MeetingPreps.PrepsHost { LogEcho = Console.Out });
