﻿using Fluid.Compilation;
using Fluid.Utils;
using Parlot;
using System.Text.Encodings.Web;

namespace Fluid.Ast
{
    public sealed partial class TextSpanStatement : Statement, ICompilable
    {
        private bool _isBufferPrepared;
        private bool _isEmpty;
        private readonly object _synLock = new();
        private TextSpan _text;
        private string _buffer;

        public TextSpanStatement(in TextSpan text)
        {
            _text = text;
        }

        public TextSpanStatement(string text)
        {
            _text = new TextSpan(text);
        }

        public bool StripLeft { get; set; }
        public bool StripRight { get; set; }

        public bool NextIsTag { get; set; }
        public bool NextIsOutput { get; set; }
        public bool PreviousIsTag { get; set; }
        public bool PreviousIsOutput { get; set; }

        public ref readonly TextSpan Text => ref _text;

        private void PrepareBuffer(TemplateOptions options)
        {
            if (_isBufferPrepared)
            {
                return;
            }
            
            // Prevent two threads from stripping the same statement in case WriteToAsync is called concurrently
            lock (_synLock)
            {
                if (!_isBufferPrepared)
                {
                    var trimming = options.Trimming;
                    StripLeft |=
                        (PreviousIsTag && (trimming & TrimmingFlags.TagRight) != 0) ||
                        (PreviousIsOutput && (trimming & TrimmingFlags.OutputRight) != 0)
                        ;

                    StripRight |=
                        (NextIsTag && (trimming & TrimmingFlags.TagLeft) != 0) ||
                        (NextIsOutput && (trimming & TrimmingFlags.OutputLeft) != 0)
                        ;

                    var span = _text.Buffer;
                    var start = 0;
                    var end = _text.Length - 1;

                    // Does this text need to have its left part trimmed?
                    if (StripLeft)
                    {
                        var firstNewLine = -1;

                        for (var i = start; i <= end; i++)
                        {
                            var c = span[_text.Offset + i];

                            if (Character.IsWhiteSpaceOrNewLine(c))
                            {
                                start++;

                                if (firstNewLine == -1 && (c == '\n'))
                                {
                                    firstNewLine = start;
                                }
                            }
                            else
                            {
                                break;
                            }
                        }

                        if (!options.Greedy)
                        {
                            if (firstNewLine != -1)
                            {
                                start = firstNewLine;
                            }
                        }
                    }

                    // Does this text need to have its right part trimmed?
                    if (StripRight)
                    {
                        var lastNewLine = -1;

                        for (var i = end; i >= start; i--)
                        {
                            var c = span[_text.Offset + i];

                            if (Character.IsWhiteSpaceOrNewLine(c))
                            {
                                if (lastNewLine == -1 && c == '\n')
                                {
                                    lastNewLine = end;
                                }

                                end--;
                            }
                            else
                            {
                                break;
                            }
                        }

                        if (!options.Greedy)
                        {
                            if (lastNewLine != -1)
                            {
                                end = lastNewLine;
                            }
                        }
                    }
                    if (end - start + 1 == 0)
                    {
                        _isEmpty = true;
                    }
                    else if (start != 0 || end != _text.Length - 1)
                    {
                        var offset = _text.Offset;
                        var buffer = _text.Buffer;

                        _text = new TextSpan(buffer, offset + start, end - start + 1);
                    }

                    _buffer = _text.ToString();
                    _isBufferPrepared = true;
                }
            }
        }

        public override ValueTask<Completion> WriteToAsync(TextWriter writer, TextEncoder encoder, TemplateContext context)
        {
            if (!_isBufferPrepared)
            {
                PrepareBuffer(context.Options);
            }

            if (_isEmpty)
            {
                return new ValueTask<Completion>(Completion.Normal);
            }

            context.IncrementSteps();

            // The Text fragments are not encoded, but kept as-is

            // Since WriteAsync needs an actual buffer, we created and reused _buffer

            static async ValueTask<Completion> Awaited(Task task)
            {
                await task;
                return Completion.Normal;
            }

            var task = writer.WriteAsync(_buffer);
            if (!task.IsCompletedSuccessfully())
            {
                return Awaited(task);
            }

            return new ValueTask<Completion>(Completion.Normal);
        }

        public CompilationResult Compile(CompilationContext context)
        {
            PrepareBuffer(context.Options);

            var result = new CompilationResult();

            //context.IncrementSteps();

            // IncrementSteps();

            if (!_isEmpty)
            {
                result.StringBuilder.AppendLine($@"await {context.TextWriter}.WriteAsync(@""{_buffer}"");");
            }
            return result;
        }
    }
}
