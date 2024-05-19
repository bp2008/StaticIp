using Spectre.Console;
using System.Collections.Generic;
using System.Threading;
using System;
using System.Linq;
using System.Net;
using BPUtil;
using System.Text.RegularExpressions;
using System.Xml.Linq;

public static class StaticIp
{
	static bool exit = false;
	static Style highlightStyle = new Style().Foreground(Color.Aqua);
	static List<InterfaceConfig> interfaces = new List<InterfaceConfig>();
	static List<string> errors = new List<string>();
	static string autoSelectInterfaceName = null;

	public static void Main(string[] args)
	{
		AnsiConsole.Status().Start("StaticIp IPv4 Utility " + Globals.AssemblyVersion + "Starting Up", ctx =>
		{
			if (System.Diagnostics.Debugger.IsAttached)
				Thread.Sleep(250); // Helps start up faster, believe it or not.
		});

		try
		{
			while (!exit)
			{
				MainLoop();
			}
		}
		catch (Exception ex)
		{
			AnsiConsole.Foreground = Color.Red;
			AnsiConsole.WriteLine();
			AnsiConsole.WriteLine(ex.ToHierarchicalString());
			AnsiConsole.WriteLine();
			AnsiConsole.ResetColors();
			AnsiConsole.WriteLine("Press ENTER key to EXIT...");
			Console.ReadLine();
		}
	}
	private static void MainLoop()
	{
		AnsiConsole.Clear();

		AnsiConsole.WriteLine("StaticIp IPv4 Utility " + Globals.AssemblyVersion);
		AnsiConsole.WriteLine();
		AnsiConsole.WriteLine("Loading interface list...");

		// NOTE: If you enable DHCP on an interface where DHCP was previously off, static IPs must be manually assigned again.
		ScanInterfaces();

		AnsiConsole.Clear();

		AnsiConsole.WriteLine("StaticIp IPv4 Utility " + Globals.AssemblyVersion);
		AnsiConsole.WriteLine();

		if (errors.Count > 0)
		{
			AnsiConsole.Foreground = Color.Red;
			AnsiConsole.WriteLine();
			foreach (string error in errors)
				AnsiConsole.WriteLine(error);
			AnsiConsole.WriteLine();
			AnsiConsole.ResetColors();
			AnsiConsole.WriteLine("Press ENTER key to retry...");
			Console.ReadLine();
			return;
		}

		if (autoSelectInterfaceName != null)
		{
			InterfaceConfig iface = interfaces.FirstOrDefault(i => i.InterfaceName == autoSelectInterfaceName);
			if (iface != null)
			{
				SelectInterface(iface);
				return;
			}
		}

		List<string> choices = new List<string>();
		foreach (InterfaceConfig iface in interfaces)
		{
			choices.Add(Markup.Escape(iface.GetSelectorLine()));
		}
		choices.Add("About This App"); // Count - 3
		choices.Add("Refresh Interface List"); // Count - 2
		choices.Add("Exit (CTRL + C)"); // Count - 1

		SelectionPrompt<string> selectionPrompt = new SelectionPrompt<string>();
		selectionPrompt.Title("Select an interface.");
		selectionPrompt.AddChoices(choices);
		selectionPrompt.HighlightStyle(highlightStyle);

		string choice = selectionPrompt.Show(AnsiConsole.Console);
		int idx = choices.IndexOf(choice);
		if (idx < interfaces.Count)
		{
			InterfaceConfig iface = interfaces[idx];
			SelectInterface(iface);
		}
		else if (idx == choices.Count - 3)
		{
			AnsiConsole.Clear();
			AnsiConsole.WriteLine("StaticIp IPv4 Utility " + Globals.AssemblyVersion);
			AnsiConsole.WriteLine();
			AnsiConsole.WriteLine("This program makes it easy to assign and delete static IP addresses on network interfaces that have DHCP enabled.  To learn more about how this works, search online for \"dhcpstaticipcoexistence\".");
			AnsiConsole.WriteLine();
			AnsiConsole.WriteLine("https://github.com/bp2008/StaticIp");
			AnsiConsole.WriteLine();
			AnsiConsole.WriteLine("Press ENTER key to return to interface list...");
			Console.ReadLine();
		}
		else if (idx == choices.Count - 2)
		{
			// Do nothing; the loop will repeat.
		}
		else if (idx == choices.Count - 1)
		{
			exit = true;
		}
	}
	private static void SelectInterface(InterfaceConfig iface)
	{
		AnsiConsole.Clear();

		DumpInterfaceIPs(iface);

		List<string> choices = new List<string>()
		{
			"Add static IP",
			"Delete static IP",
			"Return to interface list"
		};

		SelectionPrompt<string> selectionPrompt = new SelectionPrompt<string>();
		selectionPrompt.Title("Choose an option.");
		selectionPrompt.AddChoices(choices);
		selectionPrompt.HighlightStyle(highlightStyle);

		string choice = selectionPrompt.Show(AnsiConsole.Console);
		int idx = choices.IndexOf(choice);

		autoSelectInterfaceName = iface.InterfaceName;
		if (idx == 0)
			AddStaticIp(iface);
		else if (idx == 1)
			DeleteStaticIp(iface);
		else
			autoSelectInterfaceName = null;
	}

	private static void DumpInterfaceIPs(InterfaceConfig iface)
	{
		string defaultGatewayStr = "";
		if (iface.DefaultGateway != null)
			defaultGatewayStr = ", Default Gateway: [aqua]" + Markup.Escape(iface.DefaultGateway.ToString()) + "[/]";

		AnsiConsole.Write(new Markup("Interface \"[aqua]" + Markup.Escape(iface.InterfaceName) + "[/]\"" + defaultGatewayStr + Environment.NewLine));

		Table table = new Table()
			.LeftAligned()
			.Border(TableBorder.Rounded);

		table.AddColumn("[aqua]Address[/]");
		table.AddColumn("[aqua]Mask[/]");
		table.AddColumn("[aqua]Source[/]");

		foreach (Address addr in iface.IpAddresses)
		{
			table.AddRow("[aqua]" + Markup.Escape(addr.ip.ToString()) + "[/]", "[yellow]" + Markup.Escape(addr.mask.ToString()) + "[/]", addr.isStatic ? "static" : "DHCP");
		}

		AnsiConsole.Write(table);
	}

	public static void AddStaticIp(InterfaceConfig iface)
	{
		AnsiConsole.WriteLine("Enter the static IPv4 Address and subnet prefix size you wish to assign.");
		AnsiConsole.WriteLine("  example: 192.168.1.2/24");
		string ipStr = AnsiConsole.Prompt(
			new TextPrompt<string>("Enter Address:")
			.PromptStyle("green")
			.ValidationErrorMessage("[red]That's not a valid age[/]")
			.Validate(str =>
			{
				if (ParseIpInput(str, out IPAddress ignored, out IPAddress ignored2))
					return ValidationResult.Success();
				else
					return ValidationResult.Error("[red]Invalid IP Address Input[/]");
			}));

		if (ParseIpInput(ipStr, out IPAddress ip, out IPAddress mask))
		{
			if (!iface.DhcpStaticIpCoexistence)
			{
				if (!RunNetsh("interface ipv4 set interface interface=\"" + iface.InterfaceName + "\" dhcpstaticipcoexistence=enabled", out string std))
					return;
				iface.DhcpStaticIpCoexistence = true;
			}
			{
				if (!RunNetsh("interface ipv4 add address \"" + iface.InterfaceName + "\" address=" + ip + " mask=" + mask, out string std))
					return;
				SuccessWait();
			}
			return;
		}
		ErrorRequireInput("Invalid input");
	}

	public static void DeleteStaticIp(InterfaceConfig iface)
	{
		SelectionPrompt<string> selectionPrompt = new SelectionPrompt<string>();
		selectionPrompt.Title("Choose an address to delete.");
		foreach (Address addr in iface.IpAddresses.Where(a => a.isStatic))
			selectionPrompt.AddChoice(Markup.Escape(addr.ip.ToString() + "/" + IPUtil.GetPrefixSizeOfMask(addr.mask)));
		selectionPrompt.AddChoice("Cancel");
		selectionPrompt.HighlightStyle(highlightStyle);

		string choice = selectionPrompt.Show(AnsiConsole.Console);
		if (choice == "Cancel")
			return;

		if (ParseIpInput(choice, out IPAddress ip, out IPAddress mask))
		{
			if (!RunNetsh("interface ipv4 delete address \"" + iface.InterfaceName + "\" address=" + ip, out string std))
				return;
			SuccessWait();
		}
		else
		{
			ErrorRequireInput("Unrecognized choice.");
		}
	}
	#region Scan Interfaces
	private static void ScanInterfaces()
	{
		interfaces = new List<InterfaceConfig>();
		errors = new List<string>();

		ScanStage1();

		foreach (InterfaceConfig iface in interfaces)
		{
			foreach (Address addr in iface.IpAddresses)
			{
				if (addr.mask == null)
					throw new Exception("Interface \"" + iface.InterfaceName + "\" has address " + addr.ip.ToString() + " which did not have a subnet mask specified.");
			}
		}

		ScanStage2();
	}
	/// <summary>
	/// Learns from `netsh interface ipv4 show addresses`
	/// </summary>
	private static void ScanStage1()
	{
		if (!RunNetsh("interface ipv4 show addresses", out string std))
			return;

		string[] lines = std.Split('\n').Select(l => l.Trim()).ToArray();

		InterfaceConfig iface = null;
		Address lastAddress = null;
		foreach (string line in lines)
		{
			Match m = Regex.Match(line, "Configuration for interface \"([^\"]*)\"", RegexOptions.IgnoreCase);
			if (m.Success)
			{
				// New interface
				if (iface != null)
				{
					interfaces.Add(iface);
					lastAddress = null;
				}
				iface = new InterfaceConfig();
				iface.InterfaceName = m.Groups[1].Value;
			}
			else
			{
				m = Regex.Match(line, "([^:]*):(.*)");
				if (m.Success)
				{
					string key = m.Groups[1].Value.Trim();
					string value = m.Groups[2].Value.Trim();
					if (key.IEquals("DHCP enabled"))
					{
						iface.DhcpEnabled = value.IEquals("Yes");
					}
					else if (key.IEquals("IP Address"))
					{
						if (IPAddress.TryParse(value, out IPAddress addr))
						{
							lastAddress = new Address();
							lastAddress.ip = addr;
							iface.IpAddresses.Add(lastAddress);
						}
						else
						{
							lastAddress = null;
							errors.Add("Unable to parse IP Address record:"
								+ Environment.NewLine + line);
						}
					}
					else if (key.IEquals("Subnet Prefix"))
					{
						if (lastAddress != null)
						{
							m = Regex.Match(value, "\\(mask (\\d+\\.\\d+\\.\\d+\\.\\d+)\\)", RegexOptions.IgnoreCase);
							if (m.Success)
							{
								if (IPAddress.TryParse(m.Groups[1].Value, out IPAddress addr))
								{
									lastAddress.mask = addr;
								}
								else
								{
									errors.Add("Unable to parse subnet mask:"
										+ Environment.NewLine + line);
								}
							}
							else
							{
								errors.Add("Unable to parse Subnet Prefix record:"
									+ Environment.NewLine + line);
							}
						}
						else
							errors.Add("Subnet Prefix key encountered but we don't know what IP Address it is associated with:"
								+ Environment.NewLine + line);
					}
					else if (key.IEquals("Default Gateway"))
					{
						if (IPAddress.TryParse(value, out IPAddress addr))
						{
							iface.DefaultGateway = addr;
						}
						else
						{
							errors.Add("Unable to parse Default Gateway record:"
								+ Environment.NewLine + line);
						}
					}
				}
			}
		}
		if (iface != null)
			interfaces.Add(iface);
	}

	/// <summary>
	/// Learns from `netsh interface ipv4 dump`
	/// </summary>
	private static void ScanStage2()
	{
		if (!RunNetsh("interface ipv4 dump", out string std))
			return;

		string[] lines = std.Split('\n').Select(l => l.Trim()).ToArray();

		foreach (string line in lines)
		{
			Match m = Regex.Match(line, "^set interface interface=\"([^\"]*)\" (.*)", RegexOptions.IgnoreCase);
			if (m.Success)
			{
				// This line defines interface options.
				string interfaceName = m.Groups[1].Value;
				InterfaceConfig iface = interfaces.FirstOrDefault(i => i.InterfaceName == interfaceName);
				if (iface != null)
				{
					Dictionary<string, string> args = ParseArguments(m.Groups[2].Value);
					if (args.TryGetValue("dhcpstaticipcoexistence", out string dhcpstaticipcoexistence) && dhcpstaticipcoexistence == "enabled")
					{
						iface.DhcpStaticIpCoexistence = true;
					}
				}
			}
			else
			{
				m = Regex.Match(line, "^add address name=\"([^\"]*)\" (.*)", RegexOptions.IgnoreCase);
				if (m.Success)
				{
					// This line defines static addresses.
					string interfaceName = m.Groups[1].Value;
					InterfaceConfig iface = interfaces.FirstOrDefault(i => i.InterfaceName == interfaceName);
					if (iface != null)
					{
						//lastAddress = iface.IpAddresses.FirstOrDefault(a=>a.ip == 
						Dictionary<string, string> args = ParseArguments(m.Groups[2].Value);
						if (args.TryGetValue("address", out string address) && args.TryGetValue("mask", out string mask))
						{
							Address addr = iface.IpAddresses.FirstOrDefault(a => a.ip.ToString() == address && a.mask.ToString() == mask);
							if (addr != null)
								addr.isStatic = true;
							else
							{
								//errors.Add("`netsh interface ipv4 dump` told us about an address that was not seen in `netsh interface ipv4 show addresses`:"
								//	+ Environment.NewLine + line);
								if (IPAddress.TryParse(address, out IPAddress ipAddress) && IPAddress.TryParse(mask, out IPAddress ipMask))
								{
									addr = new Address();
									addr.ip = ipAddress;
									addr.mask = ipMask;
									addr.isStatic = true;
									iface.IpAddresses.Add(addr);
								}
								else
								{
									errors.Add("Failed to parse address line from `netsh interface ipv4 dump`:"
										+ Environment.NewLine + line);
								}
							}
						}
					}
				}
			}
		}
	}
	#endregion
	#region Parsing Helpers
	private static Dictionary<string, string> ParseArguments(string command)
	{
		Dictionary<string, string> argsDict = new Dictionary<string, string>();
		MatchCollection matches = Regex.Matches(command, @"(""[^""]*""|[^""\s]*)=(""[^""]*""|[^""\s]*)");

		foreach (Match match in matches)
		{
			string key = match.Groups[1].Value.Replace("\"", "");
			string value = match.Groups[2].Value.Replace("\"", "");
			argsDict[key] = value;
		}

		return argsDict;
	}

	private static bool ParseIpInput(string input, out IPAddress ip, out IPAddress mask)
	{
		input = input.Trim();
		int idxSlash = input.IndexOf('/');
		if (idxSlash != -1)
		{
			if (IPAddress.TryParse(input.Substring(0, idxSlash).Trim(), out ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
			{
				if (int.TryParse(input.Substring(idxSlash + 1).Trim(), out int maskBits))
				{
					mask = IPUtil.GenerateMaskFromPrefixSize(true, maskBits);
					return true;
				}
			}
		}
		else
		{
			if (IPAddress.TryParse(input, out ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
			{
				mask = null;
				return true;
			}
		}
		ip = null;
		mask = null;
		return false;
	}
	#endregion
	private static void ErrorRequireInput(string message)
	{
		AnsiConsole.Foreground = Color.Red;
		AnsiConsole.WriteLine(message);
		AnsiConsole.ResetColors();
		AnsiConsole.WriteLine("Press ENTER key to continue...");
		Console.ReadLine();
	}
	const int successWaitMs = 1000;
	private static void SuccessWait()
	{
		int sec = (int)Math.Round(TimeSpan.FromMilliseconds(successWaitMs).TotalSeconds);
		AnsiConsole.Foreground = Color.Green;
		AnsiConsole.WriteLine("Success. Waiting " + sec + " second" + StringUtil.PluralSuffix(sec) + "...");
		AnsiConsole.ResetColors();
		Thread.Sleep(successWaitMs);
	}
	/// <summary>
	/// Runs `netsh` with the given command and returns true if it was successful (exit code 0).
	/// </summary>
	/// <param name="command">Arguments to `netsh`.</param>
	/// <param name="std">The output of `netsh`.</param>
	/// <returns></returns>
	private static bool RunNetsh(string command, out string std)
	{
		ProcessRunnerOptions processOptions = new ProcessRunnerOptions() { CreateNoWindow = true };
		int exitCode = ProcessRunner.RunProcessAndWait("netsh", command, out std, out string err, processOptions);
		if (exitCode == 0)
			return true;
		AnsiConsole.Foreground = Color.Yellow;
		AnsiConsole.WriteLine("netsh std:");
		AnsiConsole.WriteLine(std);
		AnsiConsole.Foreground = Color.Red;
		AnsiConsole.WriteLine("netsh error:");
		AnsiConsole.WriteLine(err);
		AnsiConsole.ResetColors();
		AnsiConsole.WriteLine();
		AnsiConsole.Foreground = Color.Green;
		AnsiConsole.WriteLine("Press ENTER key to continue...");
		AnsiConsole.ResetColors();
		Console.ReadLine();
		return false;
	}
	public class InterfaceConfig
	{
		public string InterfaceName { get; set; }
		public bool DhcpEnabled { get; set; }
		public List<Address> IpAddresses { get; set; } = new List<Address>();
		public IPAddress DefaultGateway { get; set; }
		public bool DhcpStaticIpCoexistence { get; set; }

		public string GetSelectorLine()
		{
			string dhcpStr = "";
			if (DhcpEnabled)
			{
				dhcpStr = " (DHCP enabled)";
			}
			int dhcpIpCount = IpAddresses.Count(a => !a.isStatic);
			if (dhcpIpCount > 0)
				dhcpStr += " [" + dhcpIpCount + " DHCP addr" + StringUtil.PluralSuffix(dhcpIpCount) + "]";

			string staticIpStr = "";
			int staticIpCount = IpAddresses.Count(a => a.isStatic);
			if (staticIpCount > 0)
				staticIpStr += " [" + staticIpCount + " static addr" + StringUtil.PluralSuffix(staticIpCount) + "]";

			return InterfaceName + dhcpStr + staticIpStr;
		}
	}
	public class Address
	{
		public IPAddress ip;
		public IPAddress mask;
		public bool isStatic;
	}
}