using System;
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

			var section = configuration.GetSection("JanitraBot");
			var options = new JanitraBotOptions(section["AccessKey"], int.Parse(section["JanitraBotId"]));

			//new JanitraBot(new Client(section["BaseUrl"]), logger, options).RunForever();
			new JanitraBot(new Client(section["BaseUrl"]), logger, options).RunOnce().Wait();
		}
	}
}