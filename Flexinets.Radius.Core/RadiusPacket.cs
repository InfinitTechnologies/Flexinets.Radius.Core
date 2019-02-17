﻿using log4net;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Flexinets.Radius.Core
{
    /// <summary>
    /// This class encapsulates a Radius packet and presents it in a more readable form
    /// </summary>
    public class RadiusPacket : IRadiusPacket
    {
        private static readonly ILog _log = LogManager.GetLogger(typeof(RadiusPacket));

        public PacketCode Code
        {
            get;
            private set;
        }
        public Byte Identifier
        {
            get;
            private set;
        }
        public Byte[] Authenticator { get; internal set; } = new Byte[16];
        public IDictionary<String, List<Object>> Attributes { get; set; } = new Dictionary<String, List<Object>>();
        public Byte[] SharedSecret
        {
            get;
            private set;
        }
        private Byte[] _requestAuthenticator;


        private RadiusPacket()
        {
        }


        /// <summary>
        /// Create a new packet with a random authenticator
        /// </summary>
        /// <param name="code"></param>
        /// <param name="identifier"></param>
        /// <param name="secret"></param>
        /// <param name="authenticator">Set authenticator for testing</param>
        public RadiusPacket(PacketCode code, Byte identifier, String secret)
        {
            Code = code;
            Identifier = identifier;
            SharedSecret = Encoding.UTF8.GetBytes(secret);

            // Generate random authenticator for access request packets
            if (Code == PacketCode.AccessRequest || Code == PacketCode.StatusServer)
            {
                using (var csp = RandomNumberGenerator.Create())
                {
                    csp.GetNonZeroBytes(Authenticator);
                }
            }

            // A Message authenticator is required in status server packets, calculated last
            if (Code == PacketCode.StatusServer)
            {
                AddAttribute("Message-Authenticator", new Byte[16]);
            }
        }


        /// <summary>
        /// Parses packet bytes and returns an IRadiusPacket
        /// </summary>
        /// <param name="packetBytes"></param>
        /// <param name="dictionary"></param>
        /// <param name="sharedSecret"></param>
        public static IRadiusPacket Parse(Byte[] packetBytes, IRadiusDictionary dictionary, Byte[] sharedSecret)
        {
            var packetLength = BitConverter.ToUInt16(packetBytes.Skip(2).Take(2).Reverse().ToArray(), 0);
            if (packetBytes.Length != packetLength)
            {
                throw new InvalidOperationException($"Packet length does not match, expected: {packetLength}, actual: {packetBytes.Length}");
            }

            var packet = new RadiusPacket
            {
                SharedSecret = sharedSecret,
                Identifier = packetBytes[1],
                Code = (PacketCode)packetBytes[0],
            };

            Buffer.BlockCopy(packetBytes, 4, packet.Authenticator, 0, 16);

            if (packet.Code == PacketCode.AccountingRequest || packet.Code == PacketCode.DisconnectRequest)
            {
                if (!packet.Authenticator.SequenceEqual(CalculateRequestAuthenticator(packet.SharedSecret, packetBytes)))
                {
                    throw new InvalidOperationException($"Invalid request authenticator in packet {packet.Identifier}, check secret?");
                }
            }

            // The rest are attribute value pairs
            var position = 20;
            var messageAuthenticatorPosition = 0;
            while (position < packetBytes.Length)
            {
                var typecode = packetBytes[position];
                var length = packetBytes[position + 1];

                if (position + length > packetLength)
                {
                    throw new ArgumentOutOfRangeException("Go home roamserver, youre drunk");
                }
                var contentBytes = new Byte[length - 2];
                Buffer.BlockCopy(packetBytes, position + 2, contentBytes, 0, length - 2);

                try
                {
                    if (typecode == 26) // VSA
                    {
                        var vsa = new VendorSpecificAttribute(contentBytes);
                        var vendorAttributeDefinition = dictionary.GetVendorAttribute(vsa.VendorId, vsa.VendorCode);
                        if (vendorAttributeDefinition == null)
                        {
                            _log.Info($"Unknown vsa: {vsa.VendorId}:{vsa.VendorCode}");
                        }
                        else
                        {
                            try
                            {
                                var content = ParseContentBytes(vsa.Value, vendorAttributeDefinition.Type, typecode, packet.Authenticator, packet.SharedSecret);
                                packet.AddAttributeObject(vendorAttributeDefinition.Name, content);
                            }
                            catch (Exception ex)
                            {
                                _log.Error($"Something went wrong with vsa {vendorAttributeDefinition.Name}", ex);
                            }
                        }
                    }
                    else
                    {
                        var attributeDefinition = dictionary.GetAttribute(typecode);
                        if (attributeDefinition.Code == 80)
                        {
                            messageAuthenticatorPosition = position;
                        }
                        try
                        {
                            var content = ParseContentBytes(contentBytes, attributeDefinition.Type, typecode, packet.Authenticator, packet.SharedSecret);
                            packet.AddAttributeObject(attributeDefinition.Name, content);
                        }
                        catch (Exception ex)
                        {
                            _log.Error($"Something went wrong with {attributeDefinition.Name}", ex);
                            _log.Debug($"Attribute bytes: {contentBytes.ToHexString()}");
                        }
                    }
                }
                catch (KeyNotFoundException)
                {
                    _log.Warn($"Attribute {typecode} not found in dictionary");
                }
                catch (Exception ex)
                {
                    _log.Error($"Something went wrong parsing attribute {typecode}", ex);
                }

                position += length;
            }

            if (messageAuthenticatorPosition != 0)
            {
                var messageAuthenticator = packet.GetAttribute<Byte[]>("Message-Authenticator");
                var temp = new Byte[16];
                var tempPacket = new Byte[packetBytes.Length];
                packetBytes.CopyTo(tempPacket, 0);
                Buffer.BlockCopy(temp, 0, tempPacket, messageAuthenticatorPosition + 2, 16);
                var calculatedMessageAuthenticator = CalculateMessageAuthenticator(tempPacket, sharedSecret);
                if (!calculatedMessageAuthenticator.SequenceEqual(messageAuthenticator))
                {
                    throw new InvalidOperationException($"Invalid Message-Authenticator in packet {packet.Identifier}");
                }
            }

            return packet;
        }


        /// <summary>
        /// Tries to get a packet from the stream. Returns true if successful
        /// Returns false if no packet could be parsed or stream is empty ie closing
        /// </summary>
        /// <param name="stream"></param>
        /// <param name="packet"></param>
        /// <param name="dictionary"></param>
        /// <returns></returns>
        public static Boolean TryParsePacketFromStream(Stream stream, out IRadiusPacket packet, IRadiusDictionary dictionary, Byte[] sharedSecret)
        {
            var packetHeaderBytes = new Byte[4];
            var i = stream.Read(packetHeaderBytes, 0, 4);
            if (i != 0)
            {
                try
                {
                    var packetLength = BitConverter.ToUInt16(packetHeaderBytes.Reverse().ToArray(), 0);
                    var packetContentBytes = new Byte[packetLength - 4];
                    stream.Read(packetContentBytes, 0, packetContentBytes.Length);

                    packet = Parse(packetHeaderBytes.Concat(packetContentBytes).ToArray(), dictionary, sharedSecret);
                    return true;
                }
                catch (Exception ex)
                {
                    _log.Warn("Unable to parse packet from stream", ex);
                }
            }

            packet = null;
            return false;
        }


        /// <summary>
        /// Parses the content and returns an object of proper type
        /// </summary>
        /// <param name="contentBytes"></param>
        /// <param name="type"></param>
        /// <param name="code"></param>
        /// <param name="authenticator"></param>
        /// <param name="sharedSecret"></param>
        /// <returns></returns>
        private static Object ParseContentBytes(Byte[] contentBytes, String type, UInt32 code, Byte[] authenticator, Byte[] sharedSecret)
        {
            switch (type)
            {
                case "string":
				case "String":
                case "tagged-string":
                    return Encoding.UTF8.GetString(contentBytes);
                case "octet":
				case "octets":
                    // If this is a password attribute it must be decrypted
                    if (code == 2)
                    {
                        return RadiusPassword.Decrypt(sharedSecret, authenticator, contentBytes);
                    }

					return contentBytes;
				case "ipaddr":
				case "ipv6addr":
					return new IPAddress(contentBytes);
				case "date":
					return DateTimeOffset.FromUnixTimeSeconds(BitConverter.ToUInt32(contentBytes.Reverse().ToArray(), 0));
				case "short":
					return BitConverter.ToUInt16(contentBytes.Reverse().ToArray(), 0);
				case "integer":
				case "signed":
                case "tagged-integer":
                    return BitConverter.ToUInt32(contentBytes.Reverse().ToArray(), 0);
				case "integer64":
					return BitConverter.ToUInt64(contentBytes.Reverse().ToArray(), 0);
				case "abinary": // Ascend binary filter format.
				case "byte": // 8-bit unsigned integer.
				case "bytes":
				case "combo-ip":
				case "ether": // Ethernet MAC address.
				case "extended":
				case "ifid": // Interface Id (hex:hex:hex).
				case "ipv4prefix": // IPv4 Prefix as given in RFC 6572.
				case "ipv6prefix": // IPv6 prefix, with mask. 2001:db8:a000:ff::/64
				case "long-extended":
				case "struct": // Fixed size structures.
				case "tlv": // Type-Length-Value (allows nested attributes).
				default:
					_log.Warn($"No content parser for type '{type}'.");
                    return null;
            }
        }


        /// <summary>
        /// Creates a response packet with code, authenticator, identifier and secret from the request packet.
        /// </summary>
        /// <param name="responseCode"></param>
        /// <returns></returns>
        public IRadiusPacket CreateResponsePacket(PacketCode responseCode)
        {
            return new RadiusPacket
            {
                Code = responseCode,
                SharedSecret = SharedSecret,
                Identifier = Identifier,
                _requestAuthenticator = Authenticator
            };
        }


        /// <summary>
        /// Gets a single attribute value with name cast to type
        /// Throws an exception if multiple attributes with the same name are found
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="name"></param>
        /// <returns></returns>
        public T GetAttribute<T>(String name)
        {
            if (Attributes.ContainsKey(name))
            {
                return (T)Attributes[name].Single();
            }

            return default(T);
        }


        /// <summary>
        /// Gets multiple attribute values with the same name cast to type
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="name"></param>
        /// <returns></returns>
        public List<T> GetAttributes<T>(String name)
        {
            if (Attributes.ContainsKey(name))
            {
                return Attributes[name].Cast<T>().ToList();
            }
            return new List<T>();
        }


        public void AddAttribute(String name, String value)
        {
            AddAttributeObject(name, value);
        }
        public void AddAttribute(String name, UInt32 value)
        {
            AddAttributeObject(name, value);
        }
        public void AddAttribute(String name, IPAddress value)
        {
            AddAttributeObject(name, value);
        }
        public void AddAttribute(String name, Byte[] value)
        {
            AddAttributeObject(name, value);
        }

        private void AddAttributeObject(String name, Object value)
        {
            if (!Attributes.ContainsKey(name))
            {
                Attributes.Add(name, new List<Object>());
            }
            Attributes[name].Add(value);
        }


        /// <summary>
        /// Get the raw packet bytes
        /// </summary>
        /// <returns></returns>
        public Byte[] GetBytes(IRadiusDictionary dictionary)
        {
            var packetBytes = new List<Byte>
            {
                (Byte)Code,
                Identifier
            };
            packetBytes.AddRange(new Byte[18]); // Placeholder for length and authenticator

            var messageAuthenticatorPosition = 0;
            foreach (var attribute in Attributes)
            {
                // todo add logic to check attribute object type matches type in dictionary?
                foreach (var value in attribute.Value)
                {
                    var contentBytes = GetAttributeValueBytes(value);
                    var headerBytes = new Byte[2];

                    var attributeType = dictionary.GetAttribute(attribute.Key);
                    switch (attributeType)
                    {
                        case DictionaryVendorAttribute _attributeType:
                            headerBytes = new Byte[8];
                            headerBytes[0] = 26; // VSA type

                            var vendorId = BitConverter.GetBytes(_attributeType.VendorId);
                            Array.Reverse(vendorId);
                            Buffer.BlockCopy(vendorId, 0, headerBytes, 2, 4);
                            headerBytes[6] = (Byte)_attributeType.VendorCode;
                            headerBytes[7] = (Byte)(2 + contentBytes.Length);  // length of the vsa part
                            break;

                        case DictionaryAttribute _attributeType:
                            headerBytes[0] = attributeType.Code;

                            // Encrypt password if this is a User-Password attribute
                            if (_attributeType.Code == 2)
                            {
                                contentBytes = RadiusPassword.Encrypt(SharedSecret, Authenticator, contentBytes);
                            }
                            else if (_attributeType.Code == 80)    // Remember the position of the message authenticator, because it has to be added after everything else
                            {
                                messageAuthenticatorPosition = packetBytes.Count;
                            }
                            break;

                        default:
                            throw new InvalidOperationException($"Unknown attribute {attribute.Key}, check spelling or dictionary");
                    }

                    headerBytes[1] = (Byte)(headerBytes.Length + contentBytes.Length);
                    packetBytes.AddRange(headerBytes);
                    packetBytes.AddRange(contentBytes);
                }
            }

            // Note the order of the bytes...
            var packetLengthBytes = BitConverter.GetBytes(packetBytes.Count);
            packetBytes[2] = packetLengthBytes[1];
            packetBytes[3] = packetLengthBytes[0];

            var packetBytesArray = packetBytes.ToArray();

            if (Code == PacketCode.AccountingRequest || Code == PacketCode.DisconnectRequest || Code == PacketCode.CoaRequest)
            {
                var authenticator = CalculateRequestAuthenticator(SharedSecret, packetBytesArray);
                Buffer.BlockCopy(authenticator, 0, packetBytesArray, 4, 16);
            }
            else
            {
                var authenticator = _requestAuthenticator != null ? CalculateResponseAuthenticator(SharedSecret, _requestAuthenticator, packetBytesArray) : Authenticator;
                Buffer.BlockCopy(authenticator, 0, packetBytesArray, 4, 16);
            }

            if (messageAuthenticatorPosition != 0)
            {
                var temp = new Byte[16];
                Buffer.BlockCopy(temp, 0, packetBytesArray, messageAuthenticatorPosition + 2, 16);
                var messageAuthenticatorBytes = CalculateMessageAuthenticator(packetBytesArray, SharedSecret);
                Buffer.BlockCopy(messageAuthenticatorBytes, 0, packetBytesArray, messageAuthenticatorPosition + 2, 16);
            }


            return packetBytesArray;
        }


        /// <summary>
        /// Gets the byte representation of an attribute object
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        private static Byte[] GetAttributeValueBytes(Object value)
        {
            switch (value)
            {
                case String _value:
                    return Encoding.UTF8.GetBytes(_value);

                case UInt32 _value:
                    var contentBytes = BitConverter.GetBytes(_value);
                    Array.Reverse(contentBytes);
                    return contentBytes;

                case Byte[] _value:
                    return _value;

                case IPAddress _value:
                    return _value.GetAddressBytes();

                default:
                    throw new NotImplementedException();
            }
        }


        /// <summary>
        /// Validates a message authenticator attribute if one exists in the packet
        /// Message-Authenticator = HMAC-MD5 (Type, Identifier, Length, Request Authenticator, Attributes)
        /// The HMAC-MD5 function takes in two arguments:
        /// The payload of the packet, which includes the 16 byte Message-Authenticator field filled with zeros
        /// The shared secret
        /// https://www.ietf.org/rfc/rfc2869.txt
        /// </summary>
        /// <returns></returns>
        private static Byte[] CalculateMessageAuthenticator(Byte[] packetBytes, Byte[] sharedSecret)
        {
            using (var md5 = new HMACMD5(sharedSecret))
            {
                return md5.ComputeHash(packetBytes);
            }
        }


        /// <summary>
        /// Creates a response authenticator
        /// Response authenticator = MD5(Code+ID+Length+RequestAuth+Attributes+Secret)
        /// Actually this means it is the response packet with the request authenticator and secret...
        /// </summary>
        /// <param name="sharedSecret"></param>
        /// <param name="requestAuthenticator"></param>
        /// <param name="packetBytes"></param>
        /// <returns>Response authenticator for the packet</returns>
        private static Byte[] CalculateResponseAuthenticator(Byte[] sharedSecret, Byte[] requestAuthenticator, Byte[] packetBytes)
        {
            var responseAuthenticator = packetBytes.Concat(sharedSecret).ToArray();
            Buffer.BlockCopy(requestAuthenticator, 0, responseAuthenticator, 4, 16);

            using (var md5 = MD5.Create())
            {
                return md5.ComputeHash(responseAuthenticator);
            }
        }


        /// <summary>
        /// Calculate the request authenticator used in accounting, disconnect and coa requests
        /// </summary>
        /// <param name="sharedSecret"></param>
        /// <param name="packetBytes"></param>
        /// <returns></returns>
        internal static Byte[] CalculateRequestAuthenticator(Byte[] sharedSecret, Byte[] packetBytes)
        {
            return CalculateResponseAuthenticator(sharedSecret, new Byte[16], packetBytes);
        }
    }
}