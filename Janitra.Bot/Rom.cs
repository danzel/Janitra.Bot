using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Serilog;

namespace Janitra.Bot
{
	class Rom
	{
		public string FullPath { get; set; }
		public string FileName => Path.GetFileName(FullPath);
		public string Sha256Hash { get; set; }

		public Rom(string fullPath, string sha256Hash)
		{
			FullPath = fullPath;
			Sha256Hash = sha256Hash;
		}

		public static List<Rom> ScanDirectory(ILogger logger, string romsPath)
		{
			var result = new List<Rom>();

			foreach (var file in Directory.EnumerateFiles(romsPath, "*.*", SearchOption.AllDirectories))
			{
				logger.Information("Hashing {filename}", Path.GetFileName(file));
				result.Add(new Rom(file, HashFile(file)));
			}

			return result;
		}

		/// <summary>
		/// Returns the SHA256 hash of the given bytes as a lowercase string
		/// </summary>
		private static string HashFile(string path)
		{
			using (var file = File.OpenRead(path))
			{
				var hashedBytes = SHA256.Create().ComputeHash(file);
				var result = BitConverter.ToString(hashedBytes);

				return result.Replace("-", "").ToLowerInvariant();
			}
		}

	}
}