using System;
using System.Collections.Generic;
using System.IO;
using Janitra.Bot.Api;
using Microsoft.Extensions.Configuration;
using Serilog;

namespace Janitra.Bot
{
	class Program
	{
		static void Main(string[] args)
		{
			// Set up configuration sources.
			var configuration = new ConfigurationBuilder()
				.SetBasePath(Path.Combine(AppContext.BaseDirectory))
				.AddJsonFile("appsettings.json", false)
				.Build();

			var logger = new LoggerConfiguration()
				.ReadFrom.Configuration(configuration)
				.CreateLogger();

			var roms = configuration["RomsDirectory"] != null ? Rom.ScanDirectory(logger, configuration["RomsDirectory"]) : null;

			var bots = new List<JanitraBot>();
			foreach (var child in configuration.GetSection("JanitraBots").GetChildren())
			{
				var options = new JanitraBotOptions(child["AccessKey"], int.Parse(child["JanitraBotId"]), child["ProfileDir"]);

				bots.Add(new JanitraBot(new Client(child["BaseUrl"]), logger, options, roms));
			}

			foreach (var bot in bots)
			{
				Console.WriteLine("Running bot " + bot.Options.JanitraBotId);
				bot.RunOnce().Wait();
			}
		}
	}
}