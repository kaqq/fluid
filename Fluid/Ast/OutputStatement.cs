﻿using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Fluid.Compilation;
using Fluid.Values;

namespace Fluid.Ast
{
    public class OutputStatement : Statement, ICompilable
    {
        public OutputStatement(Expression expression)
        {
            Expression = expression;
        }

        public Expression Expression { get; }

        public IList<FilterExpression> Filters { get ; }

        public override ValueTask<Completion> WriteToAsync(TextWriter writer, TextEncoder encoder, TemplateContext context)
        {
            static async ValueTask<Completion> Awaited(
                ValueTask<FluidValue> t,
                TextWriter w,
                TextEncoder enc,
                TemplateContext ctx)
            {
                var value = await t;
                value.WriteTo(w, enc, ctx.CultureInfo);
                return Completion.Normal;
            }

            context.IncrementSteps();

            var task = Expression.EvaluateAsync(context);
            if (task.IsCompletedSuccessfully)
            {
                task.Result.WriteTo(writer, encoder, context.CultureInfo);
                return new ValueTask<Completion>(Completion.Normal);
            }

            return Awaited(task, writer, encoder, context);
        }

        public CompilationResult Compile(CompilationContext context)
        {
            var result = new CompilationResult();
            result.IsAsync = true;

            var outputStatement = $"outputStatement_{context.NextNumber}";
            
            result.StringBuilder.AppendLine($"var {outputStatement} = (OutputStatement){context.Caller};");
            var expressionAccessor = $"{outputStatement}.Expression";
            var expressionResult = CompilationHelpers.CompileExpression(Expression, expressionAccessor, context);

            result.StringBuilder.AppendLine(expressionResult.StringBuilder.ToString());
            result.StringBuilder.AppendLine($"{expressionResult.Result}.WriteTo({context.TextWriter}, {context.TextEncoder}, {context.TemplateContext}.CultureInfo);");

            return result;
        }

    }
}
