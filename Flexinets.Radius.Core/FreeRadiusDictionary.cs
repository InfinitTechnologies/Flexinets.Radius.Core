using Flexinets.Radius.Core;
using log4net;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Flexinets.Radius.Core
{
	public class FreeRadiusDictionary : IRadiusDictionary
	{
		private readonly List<DictionaryVendorAttribute> vendorSpecificAttributes = new List<DictionaryVendorAttribute>();
		private readonly Dictionary<byte, DictionaryAttribute> attributes = new Dictionary<byte, DictionaryAttribute>();
		private readonly Dictionary<string, DictionaryAttribute> attributeNames = new Dictionary<string, DictionaryAttribute>();
		private readonly ILog log = LogManager.GetLogger(typeof(FreeRadiusDictionary));

		public FreeRadiusDictionary(string dictionaryFilePath)
		{
			ParseFile(dictionaryFilePath);

			//var types =
			//	attributes.Select(a => a.Value.Type)
			//	.Union(attributeNames.Select(a => a.Value.Type))
			//	.Union(vendorSpecificAttributes.Select(a => a.Type))
			//	.Distinct()
			//	.OrderBy(t => t)
			//	.ToList();

			log.Info($"Parsed {attributes.Count:N0} attributes and {vendorSpecificAttributes.Count:N0} vendor attributes from file.");
		}

		public DictionaryAttribute GetAttribute(byte code)
		{
			if (!attributes.ContainsKey(code))
			{
				log.Warn($"Attribute with code '{Convert.ToString(code)}' was not found.");
			}

			return attributes[code];
		}

		public DictionaryAttribute GetAttribute(string name)
		{
			if (!attributeNames.ContainsKey(name))
			{
				log.Warn($"Attribute with name '{Convert.ToString(name)}' was not found.");
			}

			attributeNames.TryGetValue(name, out var attributeType);

			return attributeType;
		}

		public DictionaryVendorAttribute GetVendorAttribute(uint vendorId, byte vendorCode)
		{
			var attribute = vendorSpecificAttributes.FirstOrDefault(a => a.VendorId == vendorId && a.VendorCode == vendorCode);
			if (attribute == null)
			{
				log.Warn($"No attribute found for vendor id '{vendorId}' and code '{Convert.ToInt32(vendorCode)}'.");
			}

			return attribute;
		}

		private void ParseFile(string dictionaryFilePath)
		{
			using (var reader = new StreamReader(dictionaryFilePath))
			{
				var vendorName = string.Empty;
				uint vendorId = 0;

				while (!reader.EndOfStream)
				{
					var line = reader.ReadLine();
					if (line.StartsWith("$INCLUDE"))
					{
						var lineParts = SplitLine(line);
						var fileName = lineParts[1];
						var filePath = Path.Combine(Path.GetDirectoryName(dictionaryFilePath), fileName);
						ParseFile(filePath);
					}
					else if (line.StartsWith("VENDOR"))
					{
						var lineParts = SplitLine(line);
						vendorName = lineParts[1];
						vendorId = Convert.ToUInt32(lineParts[2]);
					}
					else if (line.StartsWith("END-VENDOR"))
					{
						vendorName = string.Empty;
						vendorId = 0;
					}
					else if (line.StartsWith("ATTRIBUTE"))
					{
						var lineParts = SplitLine(line);
						if (vendorId == 0)
						{
							var name = lineParts[1];
							if (!byte.TryParse(lineParts[2], out var code))
							{
								continue;
							}

							var type = lineParts[3];
							var attribute = new DictionaryAttribute(name, code, type);
							attributes[code] = attribute;
							attributeNames[name] = attribute;
						}
						else
						{
							var name = lineParts[1];
							if (!uint.TryParse(lineParts[2], out var code))
							{
								continue;
							}

							var type = lineParts[3];
							var attribute = new DictionaryVendorAttribute(vendorId, name, code, type);
							vendorSpecificAttributes.Add(attribute);
						}
					}
				}
			}
		}

		private string[] SplitLine(string line)
		{
			return line.Split(new char[] { '\t', '\x20' }, StringSplitOptions.RemoveEmptyEntries);
		}
	}
}
