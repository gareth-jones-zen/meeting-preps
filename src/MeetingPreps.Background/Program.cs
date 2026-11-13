// meeting-preps-background.exe: no window, ever. What the logon task runs (`watch`); anything else runs as the console
// program would, with nothing shown.
return MeetingPreps.Cli.Run(args, new MeetingPreps.PrepsHost { Out = TextWriter.Null, Err = TextWriter.Null, LogEcho = null });
