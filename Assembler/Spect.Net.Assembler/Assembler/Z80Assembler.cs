﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Antlr4.Runtime;
using Spect.Net.Assembler.Generated;
using Spect.Net.Assembler.SyntaxTree;
using Z80AsmParser = Spect.Net.Assembler.Generated.Z80AsmParser;

// ReSharper disable UsePatternMatching

// ReSharper disable JoinNullCheckWithUsage

namespace Spect.Net.Assembler.Assembler
{
    /// <summary>
    /// This class implements the Z80 assembler
    /// </summary>
    public partial class Z80Assembler
    {
        /// <summary>
        /// The file name of a direct text compilation
        /// </summary>
        public const string NOFILE_ITEM = "#";

        private AssemblerOptions _options;
        private AssemblerOutput _output;

        /// <summary>
        /// The condition symbols
        /// </summary>
        public HashSet<string> ConditionSymbols { get; private set; } = new HashSet<string>();

        /// <summary>
        /// Lines after running the preprocessor
        /// </summary>
        public List<SourceLineBase> PreprocessedLines { get; private set; }

        /// <summary>
        /// This method compiles the Z80 Assembly code in the specified file into Z80
        /// binary code.
        /// </summary>
        /// <param name="filename">Z80 assembly source file</param>
        /// <param name="options">
        /// Compilation options. If null is passed, the compiler uses the
        /// default options
        /// </param>
        /// <exception cref="ArgumentNullException"></exception>
        /// <returns>
        /// Output of the compilation
        /// </returns>
        public AssemblerOutput CompileFile(string filename, AssemblerOptions options = null)
        {
            var fi = new FileInfo(filename);
            var fullName = fi.FullName;
            var sourceText = File.ReadAllText(fullName);
            return DoCompile(new SourceFileItem(fullName), sourceText, options);
        }

        /// <summary>
        /// This method compiles the passed Z80 Assembly code into Z80
        /// binary code.
        /// </summary>
        /// <param name="sourceText">Source code text</param>
        /// <param name="options">
        /// Compilation options. If null is passed, the compiler uses the
        /// default options
        /// </param>
        /// <exception cref="ArgumentNullException"></exception>
        /// <returns>
        /// Output of the compilation
        /// </returns>
        public AssemblerOutput Compile(string sourceText, AssemblerOptions options = null) 
            => DoCompile(new SourceFileItem(NOFILE_ITEM), sourceText, options);


        /// <summary>
        /// This method compiles the passed Z80 Assembly code into Z80
        /// binary code.
        /// </summary>
        /// <param name="sourceItem"></param>
        /// <param name="sourceText">Source code text</param>
        /// <param name="options">
        ///     Compilation options. If null is passed, the compiler uses the
        ///     default options
        /// </param>
        /// <exception cref="ArgumentNullException"></exception>
        /// <returns>
        /// Output of the compilation
        /// </returns>
        private AssemblerOutput DoCompile(SourceFileItem sourceItem, string sourceText, 
            AssemblerOptions options = null)
        {
            // --- Init the compilation process
            if (sourceText == null)
            {
                throw new ArgumentNullException(nameof(sourceText));
            }
            _options = options ?? new AssemblerOptions();
            ConditionSymbols = new HashSet<string>(_options.PredefinedSymbols);
            _output = new AssemblerOutput(sourceItem);
            _output.StartCompilation();

            // --- Do the compilation phases
            if (ExecuteParse(0, sourceItem, sourceText, out var lines)
                && EmitCode(lines)
                && FixupSymbols())
            {
                _output.CompleteCompilation();
            }
            else
            {
                // --- Compilation failed, remove segments
                _output.Segments.Clear();
            }
            PreprocessedLines = lines;
            return _output;
        }

        #region Parsing and Directive processing

        /// <summary>
        /// Parses the source code passed to the compiler
        /// </summary>
        /// <param name="fileIndex">File index to use for source map information</param>
        /// <param name="sourceItem">Source file item</param>
        /// <param name="sourceText">Source text to parse</param>
        /// <param name="parsedLines"></param>
        /// <returns>True, if parsing was successful</returns>
        private bool ExecuteParse(int fileIndex, SourceFileItem sourceItem, string sourceText, 
            out List<SourceLineBase> parsedLines)
        {
            // --- No lines has been parsed yet
            parsedLines = new List<SourceLineBase>();

            // --- Parse all source codelines
            var inputStream = new AntlrInputStream(sourceText);
            var lexer = new Z80AsmLexer(inputStream);
            var tokenStream = new CommonTokenStream(lexer);
            var parser = new Z80AsmParser(tokenStream);
            var context = parser.compileUnit();
            var visitor = new Z80AsmVisitor();
            visitor.Visit(context);
            var visitedLines = visitor.Compilation;

            // --- Collect syntax errors
            foreach (var error in parser.SyntaxErrors)
            {
                ReportError(error);
            }

            // --- Exit if there are any errors
            if (_output.ErrorCount != 0)
            {
                return false;
            }

            // --- Now, process directives
            var currentLineIndex = 0;
            var ifdefStack = new Stack<bool?>();
            var processOps = true;
            parsedLines = new List<SourceLineBase>();

            // --- Traverse through parsed lines
            while (currentLineIndex < visitedLines.Lines.Count)
            {
                var line = visitedLines.Lines[currentLineIndex];
                if (line is IncludeDirective incDirective)
                {
                    // --- Parse the included file
                    if (!ApplyIncludeDirective(incDirective, fileIndex + 1, sourceItem,
                        out var includedLines))
                    {
                        // --- Exit if the include file contains syntax errors
                        break;
                    }

                    // --- Add the parse resutl of the include file to the result
                    var childIndex = _output.SourceFileList.Count - 1;
                    foreach (var includeLine in includedLines)
                    {
                        includeLine.FileIndex = childIndex;
                        parsedLines.Add(includeLine);
                    }
                }
                else if (line is Directive preProc)
                {
                    ApplyDirective(preProc, ifdefStack, ref processOps);
                }
                else if (processOps)
                {
                    line.FileIndex = fileIndex;
                    parsedLines.Add(line);
                }
                currentLineIndex++;
            }

            // --- Check if all #if and #ifdef has a closing #endif tag
            if (ifdefStack.Count > 0 && visitedLines.Lines.Count > 0)
            {
                ReportError(Errors.Z0062, visitedLines.Lines.Last());
            }

            return _output.ErrorCount == 0;
        }

        /// <summary>
        /// Loads and parses the file according the the #include directive
        /// </summary>
        /// <param name="incDirective">Directive with the file</param>
        /// <param name="fileIndex">File index to use for the include file</param>
        /// <param name="sourceItem">Source file item</param>
        /// <param name="parsedLines">Collection of source code lines</param>
        private bool ApplyIncludeDirective(IncludeDirective incDirective, int fileIndex, 
            SourceFileItem sourceItem,
            out List<SourceLineBase> parsedLines)
        {
            parsedLines = new List<SourceLineBase>();

            // --- Check the #include directive
            var filename = incDirective.Filename.Trim();
            if (filename.StartsWith("<") && filename.EndsWith(">"))
            {
                // TODO: System include file
                filename = filename.Substring(1, filename.Length - 2);
            }

            // --- Now, we have the file name, calculate the path
            if (sourceItem.Filename != NOFILE_ITEM)
            {
                // --- The file name is taken into account as relative
                var dirname = Path.GetDirectoryName(sourceItem.Filename) ?? string.Empty;
                filename = Path.Combine(dirname, filename);
            }

            // --- Check for file existence
            if (!File.Exists(filename))
            {
                ReportError(Errors.Z0300, incDirective, filename);
                return false;
            }

            var fi = new FileInfo(filename);
            var fullName = fi.FullName;

            // --- Check for repetition
            var childItem = new SourceFileItem(fullName);
            if (sourceItem.ContainsInIncludeList(childItem))
            {
                ReportError(Errors.Z0302, incDirective, filename);
                return false;
            }

            // --- Check for circular reference
            if (!sourceItem.Include(childItem))
            {
                ReportError(Errors.Z0303, incDirective, filename);
                return false;
            }

            // --- Now, add the included item to the output
            _output.SourceFileList.Add(childItem);

            // --- Read the include file
            string sourceText;
            try
            {
                sourceText = File.ReadAllText(filename);
            }
            catch (Exception ex)
            {
                ReportError(Errors.Z0301, incDirective, filename, ex.Message);
                return false;
            }

            // --- Parse the file
            return ExecuteParse(fileIndex, childItem, sourceText, out parsedLines);
        }

        /// <summary>
        /// Apply the specified prprocessor directive, and modify the
        /// current line index accordingly
        /// </summary>
        /// <param name="directive">Preprocessor directive</param>
        /// <param name="ifdefStack">Stack the holds #if/#ifdef information</param>
        /// <param name="processOps"></param>
        private void ApplyDirective(Directive directive, Stack<bool?> ifdefStack, ref bool processOps)
        {
            if (directive.Mnemonic == "#DEFINE" && processOps)
            {
                // --- Define a symbol
                ConditionSymbols.Add(directive.Identifier);
            }
            else if (directive.Mnemonic == "#UNDEF" && processOps)
            {
                // --- Remove a symbol
                if (processOps) ConditionSymbols.Remove(directive.Identifier);
            }
            else if (directive.Mnemonic == "#IFDEF" || directive.Mnemonic == "#IFNDEF" 
                || directive.Mnemonic == "#IF")
            {
                // --- Evaluate the condition and stop/start processing
                // --- operations accordingly
                if (processOps)
                {
                    if (directive.Mnemonic == "#IF")
                    {
                        var value = EvalImmediate(directive, directive.Expr);
                        processOps = value != null && value.Value != 0;
                    }
                    else
                    {
                        processOps = ConditionSymbols.Contains(directive.Identifier) ^
                                      directive.Mnemonic == "#IFNDEF";
                    }
                    ifdefStack.Push(processOps);
                }
                else
                {
                    // --- Do not process after #else or #endif
                    ifdefStack.Push(null);
                }
            }
            else if (directive.Mnemonic == "#ELSE")
            {
                if (ifdefStack.Count == 0)
                {
                    ReportError(Errors.Z0060, directive);
                }
                else
                {
                    // --- Process operations according to the last
                    // --- condition's value
                    var peekVal = ifdefStack.Pop();
                    if (peekVal.HasValue)
                    {
                        processOps = !peekVal.Value;
                        ifdefStack.Push(processOps);
                    }
                    else
                    {
                        ifdefStack.Push(null);
                    }
                }
            }
            else if (directive.Mnemonic == "#ENDIF")
            {
                if (ifdefStack.Count == 0)
                {
                    ReportError(Errors.Z0061, directive);
                }
                else
                {
                    // --- It is the end of an #ifden/#ifndef block
                    ifdefStack.Pop();
                    // ReSharper disable once PossibleInvalidOperationException
                    processOps = ifdefStack.Count == 0 || ifdefStack.Peek().HasValue && ifdefStack.Peek().Value;
                }
            }
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Translates a Z80AsmParserErrorInfo instance into an error
        /// </summary>
        private void ReportError(Z80AsmParserErrorInfo error)
        {
            _output.Errors.Add(new AssemblerErrorInfo(error));
        }

        /// <summary>
        /// Reports the specified error
        /// </summary>
        /// <param name="errorCode">Code of error</param>
        /// <param name="line">Source line associated with the error</param>
        /// <param name="parameters">Optiona error message parameters</param>
        private void ReportError(string errorCode, SourceLineBase line, params object[] parameters)
        {
            _output.Errors.Add(new AssemblerErrorInfo(errorCode, line, parameters));
        }

        #endregion
    }
}