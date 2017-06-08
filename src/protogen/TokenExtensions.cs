﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace ProtoBuf
{
    internal static class TokenExtensions
    {
        public static void Consume(this Peekable<Token> tokens, TokenType type, string value)
        {
            var token = tokens.Read();
            token.Assert(type, value);
            tokens.Consume();
        }
        public static bool ConsumeIf(this Peekable<Token> tokens, TokenType type, string value)
        {
            if (tokens.Peek(out var token) && token.Is(type, value))
            {
                tokens.Consume();
                return true;
            }
            return false;
        }

        public static Token Read(this Peekable<Token> tokens)
        {
            if (!tokens.Peek(out Token val))
            {
                throw new ParserException(tokens.Previous, "Unexpected end of file", true);
            }
            return val;
        }
        public static bool SkipToEndOptions(this Peekable<Token> tokens)
        {
            while (tokens.Peek(out var token))
            {
                if (token.Is(TokenType.Symbol, ";") || token.Is(TokenType.Symbol, "}"))
                    return true; // but don't consume

                tokens.Consume();
                if (token.Is(TokenType.Symbol, "]"))
                    return true;
            }
            return false;
        }
        public static bool SkipToEndStatement(this Peekable<Token> tokens)
        {
            while (tokens.Peek(out var token))
            {
                if (token.Is(TokenType.Symbol, "}"))
                    return true; // but don't consume

                tokens.Consume();
                if (token.Is(TokenType.Symbol, ";"))
                    return true;
            }
            return false;
        }
        public static bool SkipToEndObject(this Peekable<Token> tokens) => SkipToSymbol(tokens, "}");
        private static bool SkipToSymbol(this Peekable<Token> tokens, string symbol)
        {
            while (tokens.Peek(out var token))
            {
                tokens.Consume();
                if (token.Is(TokenType.Symbol, symbol))
                    return true;
            }
            return false;
        }
        public static bool SkipToEndStatementOrObject(this Peekable<Token> tokens)
        {
            while (tokens.Peek(out var token))
            {
                tokens.Consume();
                if (token.Is(TokenType.Symbol, "}") || token.Is(TokenType.Symbol, ";"))
                    return true;
            }
            return false;
        }
        public static string Consume(this Peekable<Token> tokens, TokenType type)
        {
            var token = tokens.Read();
            token.Assert(type);
            string s = token.Value;
            tokens.Consume();
            return s;
        }

        static class EnumCache<T>
        {
            private static readonly Dictionary<string, T> lookup;
            public static bool TryGet(string name, out T value) => lookup.TryGetValue(name, out value);
            static EnumCache()
            {
                var fields = typeof(T).GetFields(BindingFlags.Static | BindingFlags.Public);
                var tmp = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
                foreach (var field in fields)
                {
                    string name = field.Name;
                    var attrib = (ProtoEnumAttribute)field.GetCustomAttributes(false).FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(attrib?.Name)) name = attrib.Name;
                    var val = (T)field.GetValue(null);
                    tmp.Add(name, val);
                }
                lookup = tmp;
            }
        }
        internal static T ConsumeEnum<T>(this Peekable<Token> tokens, bool ignoreCase = true) where T : struct
        {
            var token = tokens.Read();
            var value = tokens.ConsumeString();

            if (!EnumCache<T>.TryGet(token.Value, out T val))
                token.Throw("Unable to parse " + typeof(T).Name);
            return val;
        }

        internal static int ConsumeInt32(this Peekable<Token> tokens, int? max = null)
        {
            var token = tokens.Read();
            token.Assert(TokenType.AlphaNumeric);
            tokens.Consume();
            if (max.HasValue && token.Value == "max") return max.Value;

            int val;

            if (token.Value.StartsWith("-0x", StringComparison.OrdinalIgnoreCase) && int.TryParse(token.Value.Substring(3), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out val))
                return -val;

            if (token.Value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && int.TryParse(token.Value.Substring(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out val))
                return val;

            if (int.TryParse(token.Value, NumberStyles.Integer | NumberStyles.AllowLeadingSign | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out val))
                return val;

            throw token.Throw("Unable to parse integer");
        }

        internal static string ConsumeString(this Peekable<Token> tokens, bool asBytes = false)
        {
            var token = tokens.Read();
            switch (token.Type)
            {
                case TokenType.StringLiteral:
                    MemoryStream ms = null;
                    do
                    {
                        ReadStringBytes(ref ms, token.Value);
                        tokens.Consume();
                    } while (tokens.Peek(out token) && token.Type == TokenType.StringLiteral); // literal concat is a thing
                    if (ms == null) return "";

                    if (!asBytes)
                    {
#if NETSTANDARD1_3
                        string s = ms.TryGetBuffer(out var segment)
                            ? Encoding.UTF8.GetString(segment.Array, segment.Offset, segment.Count)
                            : Encoding.UTF8.GetString(ms.ToArray());

#else
                        string s = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
#endif
                        return s.Replace("\\", @"\\")
                            .Replace("\'", @"\'")
                            .Replace("\"", @"\""")
                            .Replace("\r", @"\r")
                            .Replace("\n", @"\n")
                            .Replace("\t", @"\t");
                    }

                    var sb = new StringBuilder((int)ms.Length);
                    int b;
                    ms.Position = 0;
                    while ((b = ms.ReadByte()) >= 0)
                    {
                        switch (b)
                        {
                            case '\n': sb.Append(@"\n"); break;
                            case '\r': sb.Append(@"\r"); break;
                            case '\t': sb.Append(@"\t"); break;
                            case '\'': sb.Append(@"\'"); break;
                            case '\"': sb.Append(@"\"""); break;
                            case '\\': sb.Append(@"\\"); break;
                            default:
                                if (b >= 32 && b < 127)
                                {
                                    sb.Append((char)b);
                                }
                                else
                                {
                                    // encode as 3-part octal
                                    sb.Append('\\')
                                          .Append((char)(((b >> 6) & 7) + (int)'0'))
                                          .Append((char)(((b >> 3) & 7) + (int)'0'))
                                          .Append((char)(((b >> 0) & 7) + (int)'0'));
                                }
                                break;
                        }
                    }
                    return sb.ToString();
                case TokenType.AlphaNumeric:
                    tokens.Consume();
                    return token.Value;
                default:
                    throw token.Throw();
            }
        }

        // the normalized output *includes* the slashes, but expands octal to 3 places;
        // it is the job of codegen to change this normalized form to the target language form
        private static void ReadStringBytes(ref MemoryStream ms, string value)
        {
            void AppendAscii(MemoryStream target, string ascii)
            {
                foreach (char c in ascii)
                    target.WriteByte(checked((byte)c));
            }
            void AppendByte(MemoryStream target, ref uint codePoint, ref int len)
            {
                if (len != 0)
                {
                    target.WriteByte(checked((byte)codePoint));
                }
                codePoint = 0;
                len = 0;
            }
            unsafe void AppendNormalized(MemoryStream target, ref uint codePoint, ref int len)
            {
                if (len == 0)
                {
                    codePoint = 0;
                    return;
                }
                byte* b = stackalloc byte[10];
                char* c = stackalloc char[1];
                c[0] = checked((char)codePoint);
                int count = Encoding.UTF8.GetBytes(c, 1, b, 10);
                for (int i = 0; i < count; i++)
                {
                    target.WriteByte(b[i]);
                }
            }
            void AppendEscaped(MemoryStream target, char c)
            {
                uint codePoint;
                switch (c)
                {
                    // encoded as octal
                    case 'a': codePoint = '\a'; break;
                    case 'b': codePoint = '\b'; break;
                    case 'f': codePoint = '\f'; break;
                    case 'v': codePoint = '\v'; break;
                    case 't': codePoint = '\t'; break;
                    case 'n': codePoint = '\n'; break;
                    case 'r': codePoint = '\r'; break;

                    case '\\':
                    case '?':
                    case '\'':
                    case '\"':
                        codePoint = c;
                        break;
                    default:
                        codePoint = '?';
                        break;
                }
                int len = 1;
                AppendNormalized(target, ref codePoint, ref len);
            }
            bool GetHexValue(char c, out uint val, ref int len)
            {
                len++;
                if (c >= '0' && c <= '9')
                {
                    val = (uint)c - (uint)'0';
                    return true;
                }
                if (c >= 'a' && c <= 'f')
                {
                    val = 10 + (uint)c - (uint)'a';
                    return true;
                }
                if (c >= 'A' && c <= 'F')
                {
                    val = 10 + (uint)c - (uint)'A';
                    return true;
                }
                len--;
                val = 0;
                return false;
            }

            const int STATE_NORMAL = 0, STATE_ESCAPE = 1, STATE_OCTAL = 2, STATE_HEX = 3;
            int state = STATE_NORMAL;
            if (value == null || value.Length == 0) return;

            if (ms == null) ms = new MemoryStream(value.Length);
            uint escapedCodePoint = 0;
            int escapeLength = 0;
            foreach (char c in value)
            {
                switch (state)
                {
                    case STATE_ESCAPE:
                        if (c >= '0' && c <= '7')
                        {
                            state = STATE_OCTAL;
                            GetHexValue(c, out escapedCodePoint, ref escapeLength); // not a typo; all 1-char octal values are also the same in hex
                        }
                        else if (c == 'x')
                        {
                            state = STATE_HEX;
                        }
                        else if (c == 'u' || c == 'U')
                        {
                            throw new NotImplementedException("Unicode escape points: on my todo list");
                        }
                        else
                        {
                            state = STATE_NORMAL;
                            AppendEscaped(ms, c);
                        }
                        break;
                    case STATE_OCTAL:
                        if (c >= '0' && c <= '7')
                        {
                            GetHexValue(c, out var x, ref escapeLength);
                            escapedCodePoint = (escapedCodePoint << 3) | x;
                            if (escapeLength == 3)
                            {
                                AppendByte(ms, ref escapedCodePoint, ref escapeLength);
                                state = STATE_NORMAL;
                            }
                        }
                        else
                        {
                            // not an octal char - regular append
                            if (escapeLength == 0)
                            {
                                // include the malformed \x
                                AppendAscii(ms, @"\x");
                            }
                            else
                            {
                                AppendByte(ms, ref escapedCodePoint, ref escapeLength);
                            }
                            state = STATE_NORMAL;
                            goto case STATE_NORMAL;
                        }
                        break;
                    case STATE_HEX:
                        {
                            if (GetHexValue(c, out var x, ref escapeLength))
                            {
                                escapedCodePoint = (escapedCodePoint << 4) | x;
                                if (escapeLength == 2)
                                {
                                    AppendByte(ms, ref escapedCodePoint, ref escapeLength);
                                    state = STATE_NORMAL;
                                }
                            }
                            else
                            {
                                // not a hex char - regular append
                                AppendByte(ms, ref escapedCodePoint, ref escapeLength);
                                state = STATE_NORMAL;
                                goto case STATE_NORMAL;
                            }
                        }
                        break;
                    case STATE_NORMAL:
                        if (c == '\\')
                        {
                            state = STATE_ESCAPE;
                        }
                        else
                        {
                            uint codePoint = (uint)c;
                            int len = 1;
                            AppendNormalized(ms, ref codePoint, ref len);
                        }
                        break;
                    default:
                        throw new InvalidOperationException();
                }
            }
            // append any trailing escaped data
            AppendByte(ms, ref escapedCodePoint, ref escapeLength);
        }

        internal static bool ConsumeBoolean(this Peekable<Token> tokens)
        {
            var token = tokens.Read();
            token.Assert(TokenType.AlphaNumeric);
            tokens.Consume();
            if (string.Equals("true", token.Value, StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals("false", token.Value, StringComparison.OrdinalIgnoreCase)) return false;
            throw token.Throw("Unable to parse boolean");
        }

        static TokenType Identify(char c)
        {
            if (c == '"') return TokenType.StringLiteral;
            if (char.IsWhiteSpace(c)) return TokenType.Whitespace;
            if (char.IsLetterOrDigit(c)) return TokenType.AlphaNumeric;
            switch (c)
            {
                case '_':
                case '.':
                case '-':
                    return TokenType.AlphaNumeric;
            }
            return TokenType.Symbol;
        }

        public static IEnumerable<Token> RemoveCommentsAndWhitespace(this IEnumerable<Token> tokens)
        {
            int commentLineNumber = -1;
            foreach (var token in tokens)
            {
                if (commentLineNumber == token.LineNumber)
                {
                    // swallow everything else on that line
                }
                else if (token.Type == TokenType.Whitespace)
                {
                    continue;
                }
                else if (token.Type == TokenType.Symbol && token.Value.StartsWith("//"))
                {
                    commentLineNumber = token.LineNumber;
                }
                else
                {
                    yield return token;
                }
            }
        }

        static bool CanCombine(TokenType type, char prev, char next)
            => type != TokenType.Symbol || prev == next;

        public static IEnumerable<Token> Tokenize(this LineReader reader, string file)
        {
            var buffer = new StringBuilder();

            int lineNumber = 0, offset = 0;
            string line;
            string lastLine = null;
            while ((line = reader.ReadLine()) != null)
            {
                lastLine = line;
                lineNumber++;
                int columnNumber = 0, tokenStart = 1;
                char lastChar = '\0';
                TokenType type = TokenType.None;
                bool isEscaped = false;
                foreach (char c in line)
                {
                    columnNumber++;
                    if (type == TokenType.StringLiteral)
                    {
                        if (c == '"' && !isEscaped)
                        {
                            yield return new Token(buffer.ToString(), lineNumber, tokenStart, type, line, offset++, file);
                            buffer.Clear();
                            type = TokenType.None;
                        }
                        else
                        {
                            buffer.Append(c);
                            isEscaped = !isEscaped && c == '\\'; // ends an existing escape or starts a new one
                        }
                    }
                    else
                    {
                        var newType = Identify(c);
                        if (newType == type && CanCombine(type, lastChar, c))
                        {
                            buffer.Append(c);
                        }
                        else
                        {
                            if (buffer.Length != 0)
                            {
                                yield return new Token(buffer.ToString(), lineNumber, tokenStart, type, line, offset++, file);
                                buffer.Clear();
                            }
                            type = newType;
                            tokenStart = columnNumber;
                            if (newType != TokenType.StringLiteral)
                            {
                                buffer.Append(c);
                            }
                        }
                    }
                    lastChar = c;
                }

                if (buffer.Length != 0)
                {
                    yield return new Token(buffer.ToString(), lineNumber, tokenStart, type, lastLine, offset++, file);
                    buffer.Clear();
                }
            }

        }
    }
}

