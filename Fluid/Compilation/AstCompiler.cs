﻿using Fluid.Ast;
using Fluid.Ast.BinaryExpressions;
using Fluid.Parser;
using Fluid.Values;
using Microsoft.CodeAnalysis.CSharp;
using System.Reflection;
using System.Text;

namespace Fluid.Compilation
{
    /// <remarks>
    /// Security considerations:
    /// - String literals should not be able to be escaped (like SQL injection but for C#) and are encoded using SymbolDisplay.FormatLiteral
    /// </remarks>
    public class AstCompiler : AstVisitor
    {
        public AstCompiler(TemplateOptions templateOptions) 
        {
            _templateOptions = templateOptions;
        }

        public void RenderTemplate(Type modelType, string templateName, FluidTemplate template, StringBuilder builder, StringBuilder global, StringBuilder staticConstructor)
        {
            _modelType = modelType;

            IncreaseIndent();
            builder.AppendLine(@$"{_indent}public async ValueTask RenderAsync(TextWriter writer, TextEncoder encoder, TemplateContext context)");
            builder.AppendLine(@$"{_indent}{{");
            IncreaseIndent();

            _defaultIndent = _indent;

            builder.AppendLine(@$"{_indent}var model = context.Model?.ToObjectValue() as {modelType.FullName};");

            VisitTemplate(template);

            builder.Append(_localSb);
            builder.Append(_currentBuilder);
            global.Append(_staticsSb);
            staticConstructor.Append(_staticConstructorSb);

            builder.AppendLine(@$"{_indent}await writer.FlushAsync();");

            DecreaseIndent();
            builder.AppendLine(@$"{_indent}}}");
            DecreaseIndent();
        }

        private StringBuilder _currentBuilder = new();
        private readonly StringBuilder _localSb = new();
        private readonly StringBuilder _staticsSb = new();
        private readonly StringBuilder _staticConstructorSb = new();
        private readonly TemplateOptions _templateOptions;
        private readonly HashSet<string> _localVariables = new();
        private readonly HashSet<string> _modelVariables = new();
        private string _indent = new string(' ', 4);
        private string _defaultIndent = "";
        private int _indentLevel = 0;
        private Type _modelType;
        private int _locals = 0;
        private Dictionary<string, string> _macroValues = new();
        private Stack<int> _indentLevelsStack = new();
        private Stack<StringBuilder> _stringBuildersStack = new();

        public override Expression Visit(Expression expression)
        {
            // Handle case when Expression is null

            if (expression == null)
            {
                return base.Visit(new LiteralExpression(NilValue.Instance));
            }

            return base.Visit(expression);
        }

        protected internal override Statement VisitForStatement(ForStatement forStatement)
        {
            Visit(forStatement.Source);
            var source = _lastExpressionVariable;

            if (_lastExpressionIsModel)
            {
                _modelVariables.Add(forStatement.Identifier);
                WriteLine($@"foreach (var {forStatement.Identifier} in {source})");
                WriteLine($@"{{");
                IncreaseIndent();
                foreach (var statement in forStatement.Statements)
                {
                    Visit(statement);
                }
                DecreaseIndent();
                WriteLine($@"}}");
                _modelVariables.Remove(forStatement.Identifier);
            }
            else
            {
                // TODO: Potential optimizations by not defining the iterator in local scope and EnterForLoopScope if it's not used

                _localVariables.Add(forStatement.Identifier);
                WriteLine($@"context.EnterForLoopScope();");
                WriteLine($@"foreach (var {forStatement.Identifier} in {source}.Enumerate(context))");
                WriteLine($@"{{");
                IncreaseIndent();
                WriteLine($@"context.SetValue(""{forStatement.Identifier}"", {forStatement.Identifier});");

                foreach (var statement in forStatement.Statements)
                {
                    Visit(statement);
                }
                DecreaseIndent();
                WriteLine($@"}}");
                WriteLine($@"context.ReleaseScope();");
                _localVariables.Remove(forStatement.Identifier);
            }
            
            return forStatement;
        }

        protected internal override Expression VisitMemberExpression(MemberExpression memberExpression)
        {
            var identifierSegment = memberExpression.Segments.FirstOrDefault() as IdentifierSegment;
            var identifier = identifierSegment.Identifier;
            _lastExpressionIsModel = false;

            var property = _modelType.GetProperty(identifier, BindingFlags.IgnoreCase | BindingFlags.Instance | BindingFlags.Public);
            var field = _modelType.GetField(identifier, BindingFlags.IgnoreCase | BindingFlags.Instance | BindingFlags.Public);

            if (_modelVariables.Contains(identifier))
            {
                // TODO: Resolve each sub-property from the previous type
                // if the MethodInfo's case doesn't match the identifier, create a local variable that will be assigned and behave as an alias
                // e.g., fortune.id => var local = fortune.Id

                var accessor = identifier;
                foreach (var segment in memberExpression.Segments.Skip(1))
                {
                    accessor += "." + (segment as IdentifierSegment).Identifier;
                }

                _lastExpressionVariable = accessor;
                _lastExpressionIsModel = true;

                _modelVariables.Add(accessor);
            }
            else if (_localVariables.Contains(identifier))
            {
                _lastExpressionVariable = identifier;
                _lastExpressionIsModel = true;

                var accessor = identifier;
                foreach (var segment in memberExpression.Segments.Skip(1).Reverse())
                {
                    accessor = $@"await ({accessor}).GetValueAsync(""{(segment as IdentifierSegment).Identifier}"", context)";
                    _lastExpressionVariable = accessor;
                }
            }
            else if (property != null)
            {
                DeclareLocalValue($@"model.{property.Name}");
                _lastExpressionIsModel = true;
                _modelVariables.Add(_lastExpressionVariable);
            }
            else if (field != null)
            {
                DeclareLocalValue($@"model.{field.Name}");
                _lastExpressionIsModel = true;
                _modelVariables.Add(_lastExpressionVariable);
            }
            else
            {
                var accessor = $@"context.GetValue(""{identifier}"")";

                if (_macroValues.TryGetValue(identifier, out var macro))
                {
                    accessor = macro;
                }

                foreach (var segment in memberExpression.Segments.Skip(1).Reverse())
                {
                    if (segment is IdentifierSegment i)
                    {
                        accessor = $@"await ({accessor}).GetValueAsync(""{i.Identifier}"", context)";
                    }
                    else if (segment is FunctionCallSegment f)
                    {
                        if (f.Arguments.Any())
                        {
                            // If all the arguments are literal expressions, define the FunctionArguments statically
                            bool _canBeCached = true;

                            var initArguments = new List<string>();

                            foreach (var parameter in f.Arguments)
                            {
                                Visit(parameter.Expression);

                                _canBeCached = _canBeCached && parameter.Expression is LiteralExpression;
                                if (String.IsNullOrEmpty(parameter.Name))
                                {
                                    initArguments.Add($@".Add({_lastExpressionVariable})");
                                }
                                else
                                {
                                    initArguments.Add($@".Add(""{parameter.Name}"", {_lastExpressionVariable})");
                                }
                            }

                            if (_canBeCached)
                            {
                                DeclareStaticVariable($@"new FunctionArguments(){String.Concat(initArguments)}", cSharpType: "FunctionArguments", isModel: true);
                            }
                            else
                            {
                                DeclareLocalValue($@"new FunctionArguments(){String.Concat(initArguments)}", isModel: true);
                            }
                        }
                        else
                        {
                            _lastExpressionVariable = "FunctionArguments.Empty";
                            _lastExpressionIsModel = true;
                        }

                        accessor = $@"await ({accessor}).InvokeAsync({_lastExpressionVariable}, context)";
                    }
                    else if (segment is IndexerSegment d)
                    {
                        Visit(d.Expression);

                        accessor = $@"await ({accessor}).GetIndexAsync({GetLocalValue()}, context)";
                    }
                    else
                    {
                        throw new NotSupportedException("Invalid segment");
                    }
                }

                DeclareLocalValue(accessor);
            }

            return memberExpression;
        }

        protected internal override Statement VisitOutputStatement(OutputStatement outputStatement)
        {
            Visit(outputStatement.Expression);

            if (_lastExpressionIsModel)
            {
                // Dispatching a FluidValue.WriteTo call is expensive
                // Use this to wrap dotnet objects in FluidValue.
                // var wrapped = $"FluidValue.Create({_lastExpressionVariable}, context.Options)";
                // _sb.AppendLine($"{_indent}{wrapped}.WriteTo(writer, encoder, context.CultureInfo);");

                // Invokes CompiledTemplateBase.Write overloads to prevent dynamic dispatch
                WriteLine($"await WriteAsync({_lastExpressionVariable}, writer, encoder, context);");
            }
            else
            {
                WriteLine($"{_lastExpressionVariable}.WriteTo(writer, encoder, context.CultureInfo);");
            }

            return outputStatement;
        }

        protected string _lastExpressionVariable = "";
        protected bool _lastExpressionIsModel = false;
        protected FluidValues _lastExpressionType = FluidValues.Nil;

        protected internal override Expression VisitLiteralExpression(LiteralExpression literalExpression)
        {
            // Declares the a literal value pushes the resulting variable on the stack

            switch (literalExpression.Value.Type)
            {
                case FluidValues.Number:
                    var number = DeclareStaticVariable($@"NumberValue.Create({SymbolDisplay.FormatPrimitive(literalExpression.Value.ToNumberValue(), false, false)}M)", FluidValues.Number, null, false);
                    _lastExpressionVariable = number;
                    break;
                case FluidValues.String: 
                    var s = DeclareStaticVariable($@"StringValue.Create({SymbolDisplay.FormatLiteral(literalExpression.Value.ToStringValue(), true)})", FluidValues.String, null, false);
                    _lastExpressionVariable = s;
                    break;
                case FluidValues.Boolean:
                    _lastExpressionVariable = literalExpression.Value.ToBooleanValue() ? "BooleanValue.True" : "BooleanValue.False";
                    _lastExpressionIsModel = false;
                    _lastExpressionType = FluidValues.Boolean;
                    break;
                case FluidValues.Blank:
                    _lastExpressionVariable = "BlankValue.Instance";
                    _lastExpressionIsModel = false;
                    _lastExpressionType = FluidValues.Blank;
                    break;
                case FluidValues.Empty:
                    _lastExpressionVariable = "EmptyValue.Instance";
                    _lastExpressionIsModel = false;
                    _lastExpressionType = FluidValues.Empty;
                    break;
                case FluidValues.Nil:
                    _lastExpressionVariable = "NilValue.Instance";
                    _lastExpressionIsModel = false;
                    _lastExpressionType = FluidValues.Nil;
                    break;
                default:
                    throw new NotSupportedException("Invalid literal expression");
            }

            return literalExpression;
        }

        protected internal override Expression VisitRangeExpression(RangeExpression rangeExpression)
        {
            Visit(rangeExpression.From);
            var from = GetLocalObject(FluidValues.Number);

            Visit(rangeExpression.To);
            var to = GetLocalObject(FluidValues.Number);

            DeclareLocalValue($@"BuildRangeArray((int){from}, (int){to})", FluidValues.Array, false);

            return rangeExpression;
        }

        protected internal override Statement VisitTextSpanStatement(TextSpanStatement textSpanStatement)
        {
            // Apply all trim options to the string

            textSpanStatement.PrepareBuffer(_templateOptions);

            if (String.IsNullOrEmpty(textSpanStatement._preparedBuffer))
            {
                return textSpanStatement;
            }

            WriteLine($@"await writer.WriteAsync({SymbolDisplay.FormatLiteral(textSpanStatement.Buffer, true)});");

            return textSpanStatement;
        }

        protected internal override Expression VisitEqualBinaryExpression(EqualBinaryExpression equalBinaryExpression)
        {
            Visit(equalBinaryExpression.Left);
            var left = GetLocalValue();

            Visit(equalBinaryExpression.Right);
            var right = GetLocalValue();

            DeclareLocalValue($@"{left}.Equals({right})", FluidValues.Boolean, true);
            
            return equalBinaryExpression;
        }

        protected internal override Expression VisitNotEqualBinaryExpression(NotEqualBinaryExpression notEqualBinaryExpression)
        {
            Visit(notEqualBinaryExpression.Left);
            var left = GetLocalValue();

            Visit(notEqualBinaryExpression.Right);
            var right = GetLocalValue();

            DeclareLocalValue($@"!{left}.Equals({right})", FluidValues.Boolean, true);

            return notEqualBinaryExpression;
        }

        protected internal override Expression VisitAndBinaryExpression(AndBinaryExpression andBinaryExpression)
        {
            Visit(andBinaryExpression.Left);
            var left = GetLocalObject(FluidValues.Boolean);

            Visit(andBinaryExpression.Right);
            var right = GetLocalObject(FluidValues.Boolean);

            DeclareLocalValue($@"({left} && {right})", FluidValues.Boolean, true);

            return andBinaryExpression;
        }

        protected internal override Expression VisitOrBinaryExpression(OrBinaryExpression orBinaryExpression)
        {
            Visit(orBinaryExpression.Left);
            var left = GetLocalObject(FluidValues.Boolean);

            Visit(orBinaryExpression.Right);
            var right = GetLocalObject(FluidValues.Boolean);

            DeclareLocalValue($@"({left} || {right})", FluidValues.Boolean, true);

            return orBinaryExpression;
        }

        protected internal override Expression VisitContainsBinaryExpression(ContainsBinaryExpression containsBinaryExpression)
        {
            Visit(containsBinaryExpression.Left);
            var left = GetLocalValue();

            Visit(containsBinaryExpression.Right);
            var right = GetLocalValue();

            DeclareLocalValue($@"{left}.Contains({right})", FluidValues.Boolean, true);

            return containsBinaryExpression;
        }

        protected internal override Expression VisitLowerThanBinaryExpression(LowerThanBinaryExpression lowerThanExpression)
        {
            Visit(lowerThanExpression.Left);
            var left = GetLocalValue();

            Visit(lowerThanExpression.Right);
            var right = GetLocalValue();

            DeclareLocalValue($@"LowerThanBinaryExpression.IsLower({left}, {right}, {lowerThanExpression.Strict.ToString().ToLowerInvariant()})", FluidValues.Boolean, true);

            return lowerThanExpression;
        }

        protected internal override Expression VisitGreaterThanBinaryExpression(GreaterThanBinaryExpression greaterThanExpression)
        {
            Visit(greaterThanExpression.Left);
            var left = GetLocalValue();

            Visit(greaterThanExpression.Right);
            var right = GetLocalValue();

            DeclareLocalValue($@"GreaterThanBinaryExpression.IsGreater({left}, {right}, {greaterThanExpression.Strict.ToString().ToLowerInvariant()})", FluidValues.Boolean, true);

            return greaterThanExpression;
        }

        protected internal override Expression VisitStartsWithBinaryExpression(StartsWithBinaryExpression startsWithBinaryExpression)
        {
            Visit(startsWithBinaryExpression.Left);
            var left = GetLocalValue();

            Visit(startsWithBinaryExpression.Right);
            var right = GetLocalValue();

            DeclareLocalValue($@"await StartsWithBinaryExpression.StartsWithAsync({left}, {right}, context)", FluidValues.Boolean, true);

            return startsWithBinaryExpression;
        }

        protected internal override Expression VisitEndsWithBinaryExpression(EndsWithBinaryExpression endsWithBinaryExpression)
        {
            Visit(endsWithBinaryExpression.Left);
            var left = GetLocalValue();

            Visit(endsWithBinaryExpression.Right);
            var right = GetLocalValue();

            DeclareLocalValue($@"await EndsWithBinaryExpression.EndsWithAsync({left}, {right}, context)", FluidValues.Boolean, true);

            return endsWithBinaryExpression;
        }

        protected internal override Statement VisitIfStatement(IfStatement ifStatement)
        {
            Visit(ifStatement.Condition);

            // TODO: handle completions of each statement

            WriteLine($@"if ({GetLocalObject(FluidValues.Boolean)})");

            WriteBlock(ifStatement.Statements);

            WriteLine($@"else");

            foreach (var elseIf in ifStatement.ElseIfs)
            {
                WriteLine($@"{{");

                IncreaseIndent();

                Visit(elseIf.Condition);
                WriteLine($@"if ({GetLocalObject(FluidValues.Boolean)})");

                WriteBlock(elseIf.Statements);

                if (ifStatement.Else != null && elseIf == ifStatement.ElseIfs.LastOrDefault())
                {
                    WriteLine($@"else");
                    WriteBlock(ifStatement.Else.Statements);
                }
                else
                {
                    WriteLine($@"else");
                }
            }

            foreach (var _ in ifStatement.ElseIfs)
            {
                DecreaseIndent();
                WriteLine($@"}}");
            }

            if (!ifStatement.ElseIfs.Any() && ifStatement.Else != null)
            {
                WriteBlock(ifStatement.Else.Statements);
            }

            return ifStatement;
        }

        protected internal override Expression VisitFilterExpression(FilterExpression filterExpression)
        {
            if (filterExpression.Parameters.Any())
            {
                // If all the arguments are literal expressions, define the FilterArguments statically
                bool _canBeCached = true;

                var initArguments = new List<string>();

                foreach (var parameter in filterExpression.Parameters)
                {
                    Visit(parameter.Expression);

                    _canBeCached = _canBeCached && parameter.Expression is LiteralExpression;
                    if (String.IsNullOrEmpty(parameter.Name))
                    {
                        initArguments.Add($@".Add({_lastExpressionVariable})");
                    }
                    else
                    {
                        initArguments.Add($@".Add(""{parameter.Name}"", {_lastExpressionVariable})");
                    }
                }

                if (_canBeCached)
                {
                    DeclareStaticVariable($@"new FilterArguments(){String.Concat(initArguments)}", cSharpType: "FilterArguments", isModel: true);
                }
                else
                {
                    DeclareLocalValue($@"new FilterArguments(){String.Concat(initArguments)}", isModel: true);
                }
            }
            else
            {
                _lastExpressionVariable = "FilterArguments.Empty";
                _lastExpressionIsModel = true;
            }

            var arguments = _lastExpressionVariable;

            Visit(filterExpression.Input);

            var input = _lastExpressionVariable;

            // TODO: Filters could be resolved at the beginning on the template and referenced directly

            DeclareLocalValue($@"default(FilterDelegate)", isModel: true);

            DeclareLocalValue($@"context.Options.Filters.TryGetValue(""{filterExpression.Name}"", out {_lastExpressionVariable}) ? await {_lastExpressionVariable}({input}, {arguments}, context) : {input}");
            return filterExpression;
        }

        protected internal override Statement VisitMacroStatement(MacroStatement macroStatement)
        {
            EnterIsolatedBuilder(new StringBuilder());

            IncreaseIndent();

            WriteLine($@"private static async ValueTask<FluidValue> Macro_{macroStatement.Identifier}(FunctionArguments arguments, TemplateContext context, TextEncoder encoder)");
            WriteLine($@"{{");
            IncreaseIndent();

            var initArguments = new List<string>();

            // Create default arguments as a static FunctionArguments instance
            foreach (var parameter in macroStatement.Arguments)
            {
                Visit(parameter.Expression);

                if (String.IsNullOrEmpty(parameter.Name))
                {
                    initArguments.Add($@".Add({_lastExpressionVariable})");
                }
                else
                {
                    initArguments.Add($@".Add(""{parameter.Name}"", {_lastExpressionVariable})");
                }
            }
            
            var functionArguments = DeclareStaticVariable($@"new FunctionArguments(){String.Concat(initArguments)}", cSharpType: "FunctionArguments", isModel: true);

            WriteLine($@"using var writer = new StringWriter();");

            WriteLine($@"try");
            WriteLine($@"{{");
            IncreaseIndent();
            WriteLine($@"context.EnterChildScope();");
            WriteLine($@"InitializeFunctionArguments(context, {functionArguments}, arguments);");

            WriteBlock(macroStatement.Statements, false);

            WriteLine($@"var result = writer.ToString();");

            WriteLine($@"return new StringValue(result, false);");
            DecreaseIndent();
            WriteLine($@"}}");
            WriteLine($@"finally");
            WriteLine($@"{{");
            IncreaseIndent();
            WriteLine($@"context.ReleaseScope();");
            DecreaseIndent();
            WriteLine($@"}}");
            DecreaseIndent();
            WriteLine($@"}}");
            _currentBuilder.AppendLine($@"");

            var function = _currentBuilder.ToString();

            LeaveIsolatedBuilder();

            _staticsSb.Insert(0, function);

            DeclareLocalValue($@"new MacroValue(Macro_{macroStatement.Identifier}, encoder)");

            _macroValues[macroStatement.Identifier] = _lastExpressionVariable;

            return macroStatement;
        }

        protected internal override Statement VisitBreakStatement(BreakStatement breakStatement)
        {
            WriteLine($@"break;");
            return breakStatement;
        }

        protected internal override Statement VisitContinueStatement(ContinueStatement continueStatement)
        {
            WriteLine($@"continue;");
            return continueStatement;
        }

        private void WriteBlock(IEnumerable<Statement> statements, bool renderBraces = true)
        {
            if (renderBraces)
            {
                WriteLine($@"{{");
                IncreaseIndent();
            }

            foreach (var statement in statements)
            {
                Visit(statement);
            }

            if (renderBraces)
            {
                DecreaseIndent();
                WriteLine($@"}}");
            }
        }

        private string DeclareGlobalVariable(string value, bool isModel = false)
        {
            _locals++;
            var name = $"local_{_locals}";
            _localSb.AppendLine($@"{_defaultIndent}var {name} = {value};");
            _lastExpressionVariable = name;
            _lastExpressionIsModel = isModel;
            return name;
        }

        private string DeclareStaticVariable(string value, FluidValues type = FluidValues.Nil, string cSharpType = null, bool isModel = false)
        {
            _locals++;
            var name = $"local_{_locals}";
            _staticsSb.AppendLine($@"    private static readonly {cSharpType ?? GetFluidTypeName(type)} {name};");
            _lastExpressionVariable = name;
            _lastExpressionIsModel = isModel;
            _lastExpressionType = type;

            _staticConstructorSb.AppendLine($@"        {name} = {value};");

            return name;
        }

        private string GetFluidTypeName(FluidValues type)
        {
            return type switch {
                FluidValues.Boolean => nameof(BooleanValue),
                FluidValues.Array => nameof(ArrayValue),
                FluidValues.Blank => nameof(BlankValue),
                FluidValues.String => nameof(StringValue),
                FluidValues.Number => nameof(NumberValue),
                FluidValues.Object => nameof(ObjectValue),
                FluidValues.Nil => nameof(FluidValue),
                FluidValues.Empty => nameof(EmptyValue),
                FluidValues.DateTime => nameof(DateTimeValue),
                FluidValues.Function => nameof(FunctionValue),
                _ => throw new NotSupportedException("Invalid FluidValue type")
            };
        }

        /// <summary>
        /// Return the _lastExpressionVariable value as a FluidValue
        /// </summary>
        /// <returns></returns>
        private string GetLocalValue()
        {
            var value = _lastExpressionVariable;

            if (!_lastExpressionIsModel)
            {
                return value;
            }

            return _lastExpressionType switch
            {
                FluidValues.Boolean => $"{value} ? BooleanValue.True : BooleanValue.False",
                FluidValues.String => $"StringValue.Create({value})",
                FluidValues.Number => $"NumberValue.Create({value})",
                _ => $"FluidValue.Create({_lastExpressionVariable}, context.Options)"
            };
        }

        private string GetLocalObject(FluidValues typeHint)
        {
            var value = _lastExpressionVariable;

            if (_lastExpressionIsModel)
            {
                if (_lastExpressionType == typeHint)
                {
                    return value;
                }
                else
                {
                    value = $"FluidValue.Create({_lastExpressionVariable}, context.Options)";
                }
            }

            return typeHint switch
            {
                FluidValues.Boolean => $"{value}.ToBooleanValue()",
                FluidValues.String => $"{value}.ToStringValue()",
                FluidValues.Number => $"{value}.ToNumberValue()",
                FluidValues.Array => $"await {value}.Enumerate(context)",
                _ => throw new NotSupportedException("Invalid type conversion for local value")
            };
        }

        /// <summary>
        /// Defines a new local FluidValue variable
        /// </summary>
        /// <param name="value"></param>
        /// <param name="type"></param>
        /// <param name="isModel"></param>
        /// <returns></returns>
        private string DeclareLocalValue(string value, FluidValues type = FluidValues.Nil, bool isModel = false)
        {
            _locals++;
            var name = $"local_{_locals}";
            WriteLine($@"var {name} = {value};");
            _lastExpressionVariable = name;
            _lastExpressionIsModel = isModel;
            _lastExpressionType = type;
            return name;
        }
        private void IncreaseIndent()
        {
            _indentLevel++;
            _indent = new string(' ', 4 * _indentLevel);
        }

        private void DecreaseIndent()
        {
            _indentLevel--;
            _indent = new string(' ', 4 * _indentLevel);
        }

        private void BackupIndentLevel()
        {
            _indentLevelsStack.Push(_indentLevel);
            _indentLevel = 0;
        }

        private void RestoreIndentLevel()
        {
            _indentLevel = _indentLevelsStack.Pop();
            _indent = new string(' ', 4 * _indentLevel);
        }

        private void EnterIsolatedBuilder(StringBuilder sb)
        {
            _stringBuildersStack.Push(_currentBuilder);
            _currentBuilder = sb;
            BackupIndentLevel();
        }

        private void LeaveIsolatedBuilder()
        {
            _currentBuilder = _stringBuildersStack.Pop();
            RestoreIndentLevel();
        }

        private void WriteLine(string value)
        {
            _currentBuilder.Append(_indent).AppendLine(value);
        }

        private void Write(string value)
        {
            _currentBuilder.Append(_indent).Append(value);
        }
    }
}
