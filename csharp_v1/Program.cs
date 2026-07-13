using AI_YOUTUBER.Configuration;
using AI_YOUTUBER.ConsoleApp;

Ex01Settings settings = Ex01Settings.Load();
Ex01ConsoleApplication application = new(settings);
await application.RunAsync();
