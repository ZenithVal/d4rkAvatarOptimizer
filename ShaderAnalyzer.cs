﻿#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using System.IO;
using System.Text.RegularExpressions;
using System.Text;
using System.Globalization;
using System.Linq;
using d4rkpl4y3r.Util;

namespace d4rkpl4y3r
{
    public class ParsedShader
    {
        public class Property
        {
            public enum Type
            {
                Unknown,
                Color,
                Float,
                Float4,
                Int,
                Int4,
                Texture2D,
                Texture2DArray
            }
            public string name;
            public Type type = Type.Unknown;
            public List<string> shaderLabParams = new List<string>();
        }
        public class Pass
        {
            public string vertex;
            public string hull;
            public string domain;
            public string geometry;
            public string fragment;
        }
        public string name;
        public List<string> lines = new List<string>();
        public List<Property> properties = new List<Property>();
        public List<Pass> passes = new List<Pass>();
        public Dictionary<string, string> functionReturnType = new Dictionary<string, string>();
    }

    public static class ShaderAnalyzer
    {
        private enum ParseState
        {
            Init,
            PropertyBlock,
            ShaderLab,
            CGInclude,
            CGProgram
        }

        private static Dictionary<string, ParsedShader> parsedShaderCache = new Dictionary<string, ParsedShader>();

        public static void ClearParsedShaderCache()
        {
            parsedShaderCache.Clear();
        }

        public static ParsedShader Parse(Shader shader)
        {
            if (shader == null)
                return null;
            ParsedShader parsedShader;
            if (!parsedShaderCache.TryGetValue(shader.name, out parsedShader))
            {
                maxIncludes = 50;
                parsedShader = new ParsedShader();
                parsedShader.name = shader.name;
                Profiler.StartSection("ShaderAnalyzer.RecursiveParseFile()");
                RecursiveParseFile(AssetDatabase.GetAssetPath(shader), parsedShader.lines);
                Profiler.StartNextSection("ShaderAnalyzer.SemanticParseShader()");
                SemanticParseShader(parsedShader);
                Profiler.EndSection();
                parsedShaderCache[shader.name] = parsedShader;
            }
            return parsedShader;
        }

        private static int FindEndOfStringLiteral(string text, int startIndex)
        {
            for (int i = startIndex; i < text.Length; i++)
            {
                if (text[i] == '\\')
                {
                    i++;
                }
                else if (text[i] == '"')
                {
                    return i;
                }
            }
            return -1;
        }

        private static int maxIncludes = 50;
        private static bool RecursiveParseFile(string filePath, List<string> processedLines, List<string> alreadyIncludedFiles = null)
        {
            bool isTopLevelFile = false;
            if (alreadyIncludedFiles == null)
            {
                alreadyIncludedFiles = new List<string>();
                isTopLevelFile = true;
            }
            if (--maxIncludes < 0)
            {
                Debug.LogError("Reach max include depth");
                return false;
            }
            filePath = Path.GetFullPath(filePath);
            if (alreadyIncludedFiles.Contains(filePath))
            {
                return true;
            }
            alreadyIncludedFiles.Add(filePath);
            string[] rawLines = null;
            try
            {
                rawLines = File.ReadAllLines(filePath);
            }
            catch (FileNotFoundException)
            {
                return false; //this is probably a unity include file
            }
            catch (IOException e)
            {
                Debug.LogError("Error reading shader file.  " + e.ToString());
                return false;
            }

            for (int lineIndex = 0; lineIndex < rawLines.Length; lineIndex++)
            {
                string trimmedLine = rawLines[lineIndex].Trim();
                if (trimmedLine == "")
                    continue;
                for (int i = 0; i < trimmedLine.Length - 1; i++)
                {
                    if (trimmedLine[i] == '"')
                    {
                        int end = FindEndOfStringLiteral(trimmedLine, i + 1);
                        i = (end == -1) ? trimmedLine.Length : end;
                        continue;
                    }
                    else if (trimmedLine[i] != '/')
                    {
                        continue;
                    }
                    if (trimmedLine[i + 1] == '/')
                    {
                        trimmedLine = trimmedLine.Substring(0, i).TrimEnd();
                        break;
                    }
                    else if (trimmedLine[i + 1] == '*')
                    {
                        int endCommentBlock = trimmedLine.IndexOf("*/", i + 2);
                        bool isMultiLineCommentBlock = endCommentBlock == -1;
                        while (endCommentBlock == -1 && ++lineIndex < rawLines.Length)
                        {
                            endCommentBlock = rawLines[lineIndex].IndexOf("*/");
                        }
                        if (endCommentBlock != -1)
                        {
                            if (isMultiLineCommentBlock)
                            {
                                trimmedLine = trimmedLine.Substring(0, i)
                                    + rawLines[lineIndex].Substring(endCommentBlock + 2);
                            }
                            else
                            {
                                trimmedLine = trimmedLine.Substring(0, i)
                                    + trimmedLine.Substring(endCommentBlock + 2);
                            }
                            i -= 1;
                        }
                        else
                        {
                            trimmedLine = trimmedLine.Substring(0, i).TrimEnd();
                            break;
                        }
                    }
                }
                if (trimmedLine == "")
                    continue;
                if (isTopLevelFile && (trimmedLine == "CGINCLUDE" || trimmedLine == "CGPROGRAM"))
                {
                    alreadyIncludedFiles.Clear();
                }
                if (trimmedLine.StartsWith("#include "))
                {
                    int firstQuote = trimmedLine.IndexOf('"');
                    int lastQuote = trimmedLine.LastIndexOf('"');
                    string includePath = trimmedLine.Substring(firstQuote + 1, lastQuote - firstQuote - 1);
                    includePath = Path.GetDirectoryName(filePath) + "/" + includePath;
                    if (!RecursiveParseFile(includePath, processedLines, alreadyIncludedFiles))
                    {
                        processedLines.Add(trimmedLine);
                    }
                    continue;
                }
                if (trimmedLine.EndsWith("{"))
                {
                    trimmedLine = trimmedLine.Substring(0, trimmedLine.Length - 1).TrimEnd();
                    if (trimmedLine != "")
                        processedLines.Add(trimmedLine);
                    processedLines.Add("{");
                    continue;
                }
                processedLines.Add(trimmedLine);
            }
            return true;
        }

        private static ParsedShader.Property ParseProperty(string line)
        {
            string modifiedLine = line;
            int openBracketIndex = line.IndexOf('[');
            while (openBracketIndex != -1)
            {
                int closeBracketIndex = modifiedLine.IndexOf(']') + 1;
                if (closeBracketIndex != 0)
                {
                    modifiedLine = modifiedLine.Substring(0, openBracketIndex)
                        + modifiedLine.Substring(closeBracketIndex);
                    openBracketIndex = modifiedLine.IndexOf('[');
                }
                else
                {
                    break;
                }
            }
            modifiedLine = modifiedLine.Trim();
            int parentheses = modifiedLine.IndexOf('(');
            if (parentheses != -1)
            {
                var output = new ParsedShader.Property();
                output.name = modifiedLine.Substring(0, parentheses).TrimEnd();
                int quoteIndex = modifiedLine.IndexOf('"', parentheses);
                quoteIndex = FindEndOfStringLiteral(modifiedLine, quoteIndex + 1);
                int colonIndex = modifiedLine.IndexOf(',', quoteIndex + 1);
                modifiedLine = modifiedLine.Substring(colonIndex + 1).TrimStart();
                if (modifiedLine.StartsWith("Range") || modifiedLine.StartsWith("Float"))
                {
                    output.type = ParsedShader.Property.Type.Float;
                }
                else if (modifiedLine.StartsWith("Int"))
                {
                    output.type = ParsedShader.Property.Type.Int;
                }
                else if (modifiedLine.StartsWith("Color"))
                {
                    output.type = ParsedShader.Property.Type.Color;
                }
                else if (modifiedLine.StartsWith("2DArray"))
                {
                    output.type = ParsedShader.Property.Type.Texture2DArray;
                }
                else if (modifiedLine.StartsWith("2D"))
                {
                    output.type = ParsedShader.Property.Type.Texture2D;
                }
                return output;
            }
            return null;
        }

        public static (string name, string returnType) ParseFunctionDefinition(string line)
        {
            var match = Regex.Match(line, @"^(inline\s+)?(\w+)\s+(\w+)\s*\(");
            if (match.Success && match.Groups[2].Value != "return" && match.Groups[2].Value != "else")
            {
                return (match.Groups[3].Value, match.Groups[2].Value);
            }
            return (null, null);
        }

        private static void ParsePragma(string line, ParsedShader.Pass pass)
        {
            if (!line.StartsWith("#pragma "))
                return;
            line = line.Substring("#pragma ".Length);
            var match = Regex.Match(line, @"(vertex|hull|domain|geometry|fragment)\s+(\w+)");
            if (!match.Success)
                return;
            switch (match.Groups[1].Value)
            {
                case "vertex":
                    pass.vertex = match.Groups[2].Value;
                    break;
                case "hull":
                    pass.hull = match.Groups[2].Value;
                    break;
                case "domain":
                    pass.domain = match.Groups[2].Value;
                    break;
                case "geometry":
                    pass.geometry = match.Groups[2].Value;
                    break;
                case "fragment":
                    pass.fragment = match.Groups[2].Value;
                    break;
            }
        }

        private static void SemanticParseShader(ParsedShader parsedShader)
        {
            var cgIncludePragmas = new ParsedShader.Pass();
            ParsedShader.Pass currentPass = null;
            var state = ParseState.Init;
            for (int lineIndex = 0; lineIndex < parsedShader.lines.Count; lineIndex++)
            {
                string line = parsedShader.lines[lineIndex];
                switch (state)
                {
                    case ParseState.Init:
                        if (line == "Properties")
                        {
                            state = ParseState.PropertyBlock;
                        }
                        break;
                    case ParseState.PropertyBlock:
                        if (line == "}")
                        {
                            state = ParseState.ShaderLab;
                        }
                        else
                        {
                            var property = ParseProperty(line);
                            if (property != null)
                            {
                                parsedShader.properties.Add(property);
                            }
                        }
                        break;
                    case ParseState.ShaderLab:
                        if (line == "CGINCLUDE")
                        {
                            state = ParseState.CGInclude;
                            currentPass = cgIncludePragmas;
                        }
                        else if (line == "CGPROGRAM")
                        {
                            state = ParseState.CGProgram;
                            currentPass = new ParsedShader.Pass();
                            currentPass.vertex = cgIncludePragmas.vertex;
                            currentPass.hull = cgIncludePragmas.hull;
                            currentPass.domain = cgIncludePragmas.domain;
                            currentPass.geometry = cgIncludePragmas.geometry;
                            currentPass.fragment = cgIncludePragmas.fragment;
                            parsedShader.passes.Add(currentPass);
                        }
                        else
                        {
                            var matches = Regex.Matches(line, @"\[[_a-zA-Z0-9]+\]");
                            if (matches.Count > 0)
                            {
                                string shaderLabParam = Regex.Match(line, @"^[_a-zA-Z]+").Captures[0].Value;
                                foreach (Match match in matches)
                                {
                                    string propName = match.Value.Substring(1, match.Value.Length - 2);
                                    foreach (var prop in parsedShader.properties)
                                    {
                                        if (propName == prop.name)
                                        {
                                            prop.shaderLabParams.Add(shaderLabParam);
                                        }
                                    }
                                }
                            }
                        }
                        break;
                    case ParseState.CGInclude:
                    case ParseState.CGProgram:
                        if (line == "ENDCG")
                        {
                            state = ParseState.ShaderLab;
                        }
                        var func = ParseFunctionDefinition(line);
                        if (func.name != null)
                        {
                            parsedShader.functionReturnType[func.name] = func.returnType;
                        }
                        ParsePragma(line, currentPass);
                        break;
                }
            }
        }
    }

    public class ShaderOptimizer
    {
        private enum ParseState
        {
            Init,
            PropertyBlock,
            ShaderLab,
            CGInclude,
            CGProgram
        }

        private List<string> output;
        private ParsedShader parsedShader;
        private int meshToggleCount;
        private Dictionary<string, string> staticPropertyValues;
        private Dictionary<string, (string type, List<string> values)> arrayPropertyValues;

        private ShaderOptimizer() {}

        public static List<string> Run(ParsedShader source,
            Dictionary<string, string> staticPropertyValues = null,
            int meshToggleCount = 0,
            Dictionary<string, (string type, List<string> values)> arrayPropertyValues = null)
        {
            var optimizer = new ShaderOptimizer
            {
                meshToggleCount = meshToggleCount,
                staticPropertyValues = staticPropertyValues ?? new Dictionary<string, string>(),
                arrayPropertyValues = arrayPropertyValues ?? new Dictionary<string, (string type, List<string> values)>(),
                parsedShader = source
            };
            return optimizer.Run();
        }

        private void InjectArrayPropertyInitialization()
        {
            foreach (var arrayProperty in arrayPropertyValues)
            {
                string name = "d4rkAvatarOptimizerArray" + arrayProperty.Key;
                output.Add(arrayProperty.Key + " = " + name + "[d4rkAvatarOptimizer_MaterialID];");
            }
        }

        private void InjectVertexShaderCode(
            List<string> source,
            ref int sourceLineIndex,
            (string name, string returnType) func)
        {
            string line = source[sourceLineIndex];
            string funcParams = line.Substring(line.IndexOf('(') + 1);
            output.Add(func.returnType + " " + func.name + "(");
            output.Add("float4 d4rkAvatarOptimizer_UV0 : TEXCOORD0,");
            output.Add(funcParams);
            while (line != "{" && sourceLineIndex < source.Count - 1)
            {
                line = source[++sourceLineIndex];
                output.Add(line);
            }
            if (meshToggleCount > 0)
            {
                output.Add("if (d4rkAvatarOptimizer_Zero)");
                output.Add("{");
                string val = "float val = _IsActiveMesh0";
                for (int i = 1; i < meshToggleCount; i++)
                {
                    val += " + _IsActiveMesh" + i;
                }
                output.Add(val + ";");
                output.Add("if (val) return (" + func.returnType + ")0;");
                output.Add("}");
                output.Add("if (!_IsActiveMesh[d4rkAvatarOptimizer_UV0.z])");
                output.Add("{");
                output.Add("return (" + func.returnType + ")0;");
                output.Add("}");
            }
            if (arrayPropertyValues.Count == 0)
            {
                return;
            }
            sourceLineIndex++;
            output.Add("d4rkAvatarOptimizer_MaterialID = d4rkAvatarOptimizer_UV0.w;");
            InjectArrayPropertyInitialization();
            int braceDepth = 0;
            for (; sourceLineIndex < source.Count; sourceLineIndex++)
            {
                line = source[sourceLineIndex];
                if (line == "}")
                {
                    output.Add(line);
                    if (braceDepth-- == 0)
                    {
                        return;
                    }
                }
                else if (line == "{")
                {
                    output.Add(line);
                    braceDepth++;
                }
                else if (line.StartsWith("return "))
                {
                    output.Add("{");
                    output.Add(func.returnType + " d4rkAvatarOptimizer_vertexOutput = " + line.Substring("return ".Length));
                    output.Add("d4rkAvatarOptimizer_vertexOutput.d4rkAvatarOptimizer_MaterialID = d4rkAvatarOptimizer_MaterialID;");
                    output.Add("return d4rkAvatarOptimizer_vertexOutput;");
                    output.Add("}");
                }
                else
                {
                    output.Add(line);
                }
            }
        }

        private static string FindParameterName(string line, string type)
        {
            var matches = Regex.Matches(line, @"(\w+)\s+(\w+)");
            foreach (Match match in matches)
            {
                if (match.Groups[1].Value == type)
                {
                    return match.Groups[2].Value;
                }
            }
            return null;
        }

        private void InjectFragmentShaderCode(
            List<string> source,
            ref int sourceLineIndex,
            (string name, string returnType) func,
            string v2fType)
        {
            string line = source[sourceLineIndex];
            string funcParams = line.Substring(line.IndexOf('(') + 1);
            output.Add(line);
            string v2fParamName = FindParameterName(funcParams, v2fType);
            while (line != "{" && sourceLineIndex < source.Count - 1)
            {
                line = source[++sourceLineIndex];
                if (v2fParamName == null)
                    v2fParamName = FindParameterName(line, v2fType);
                output.Add(line);
            }
            if (arrayPropertyValues.Count == 0)
            {
                return;
            }
            output.Add("d4rkAvatarOptimizer_MaterialID = " + v2fParamName + ".d4rkAvatarOptimizer_MaterialID;");
            InjectArrayPropertyInitialization();
        }

        private void InjectPropertyArrays()
        {
            if (arrayPropertyValues.Count == 0)
                return;
            output.Add("static uint d4rkAvatarOptimizer_MaterialID = 0;");
            foreach (var arrayProperty in arrayPropertyValues)
            {
                var array = arrayProperty.Value;
                string name = "d4rkAvatarOptimizerArray" + arrayProperty.Key;
                output.Add("static const " + array.type + " " + name + "[" + array.values.Count + "] = ");
                output.Add("{");
                for (int i = 0; i < array.values.Count; i++)
                {
                    output.Add(array.values[i] + ",");
                }
                output.Add("};");
                output.Add("static " + array.type + " " + arrayProperty.Key + ";");
            }
        }

        private void ParseCodeLines(List<string> source, ref int sourceLineIndex, ParsedShader.Pass pass)
        {
            var line = source[sourceLineIndex];
            var func = meshToggleCount > 0 ? ShaderAnalyzer.ParseFunctionDefinition(line) : (null, null);
            if (func.name != null && func.name == pass.vertex)
            {
                InjectVertexShaderCode(source, ref sourceLineIndex, func);
            }
            else if (func.name != null && func.name == pass.fragment)
            {
                string vertexOutput;
                parsedShader.functionReturnType.TryGetValue(pass.vertex, out vertexOutput);
                InjectFragmentShaderCode(source, ref sourceLineIndex, func, vertexOutput);
            }
            else if (arrayPropertyValues.Count > 0 && line.StartsWith("struct "))
            {
                output.Add(line);
                var match = Regex.Match(line, @"struct\s+(\w+)");
                string vertexOutput;
                if (match.Success && parsedShader.functionReturnType.TryGetValue(pass.vertex, out vertexOutput))
                {
                    if (match.Groups[1].Value == vertexOutput && source[sourceLineIndex + 1] == "{")
                    {
                        sourceLineIndex++;
                        output.Add("{");
                        output.Add("uint d4rkAvatarOptimizer_MaterialID : d4rkAvatarOptimizer_MATERIAL_ID;");
                    }
                }
            }
            else
            {
                var match = Regex.Match(line, @"(uniform\s+)?([a-zA-Z0-9_]+)\s+([a-zA-Z0-9_]+)\s*;");
                if (match.Success)
                {
                    var name = match.Groups[3].Value;
                    string value;
                    if (staticPropertyValues.TryGetValue(name, out value))
                    {
                        var type = match.Groups[2].Value;
                        output.Add("static " + type + " " + name + " = " + value + ";");
                    }
                    else if (!arrayPropertyValues.ContainsKey(name))
                    {
                        output.Add(line);
                    }
                }
                else
                {
                    output.Add(line);
                }
            }
        }

        private List<string> Run()
        {
            output = new List<string>();
            int passID = -1;
            var cgInclude = new List<string>();
            var state = ParseState.Init;
            for (int lineIndex = 0; lineIndex < parsedShader.lines.Count; lineIndex++)
            {
                string line = parsedShader.lines[lineIndex];
                switch (state)
                {
                    case ParseState.Init:
                        if (line == "Properties")
                        {
                            state = ParseState.PropertyBlock;
                        }
                        output.Add(line);
                        break;
                    case ParseState.PropertyBlock:
                        output.Add(line);
                        if (line == "}")
                        {
                            state = ParseState.ShaderLab;
                        }
                        else if (line == "{" && meshToggleCount > 0)
                        {
                            for (int i = 0; i < meshToggleCount; i++)
                            {
                                output.Add("_IsActiveMesh" + i + "(\"Generated Mesh Toggle " + i + "\", Float) = 1");
                            }
                        }
                        break;
                    case ParseState.ShaderLab:
                        if (line == "CGINCLUDE")
                        {
                            state = ParseState.CGInclude;
                        }
                        else if (line == "CGPROGRAM")
                        {
                            state = ParseState.CGProgram;
                            var pass = parsedShader.passes[++passID];
                            output.Add(line);
                            output.Add("uniform float d4rkAvatarOptimizer_Zero;");
                            InjectPropertyArrays();
                            if (meshToggleCount > 0)
                            {
                                output.Add("cbuffer d4rkAvatarOptimizer_MeshToggles");
                                output.Add("{");
                                output.Add("float _IsActiveMesh[" + meshToggleCount + "] : packoffset(c0);");
                                for (int i = 0; i < meshToggleCount; i++)
                                {
                                    output.Add("float _IsActiveMesh" + i + " : packoffset(c" + i + ");");
                                }
                                output.Add("};");
                            }
                            for (int includeLineIndex = 0; includeLineIndex < cgInclude.Count; includeLineIndex++)
                            {
                                ParseCodeLines(cgInclude, ref includeLineIndex, pass);
                            }
                        }
                        else
                        {
                            output.Add(line);
                        }
                        break;
                    case ParseState.CGInclude:
                        if (line == "ENDCG")
                        {
                            state = ParseState.ShaderLab;
                        }
                        else
                        {
                            cgInclude.Add(line);
                        }
                        break;
                    case ParseState.CGProgram:
                        if (line == "ENDCG")
                        {
                            state = ParseState.ShaderLab;
                            output.Add("ENDCG");
                        }
                        else
                        {
                            ParseCodeLines(parsedShader.lines, ref lineIndex, parsedShader.passes[passID]);
                        }
                        break;
                }
            }
            return output;
        }
    }
}
#endif