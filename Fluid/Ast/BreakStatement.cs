﻿using System.Text.Encodings.Web;

namespace Fluid.Ast
{
    public class BreakStatement : Statement
    {
        protected internal override Statement Accept(AstVisitor visitor) => visitor.VisitBreakStatement(this);

        public override ValueTask<Completion> WriteToAsync(TextWriter writer, TextEncoder encoder, TemplateContext context)
        {
            return Break();
        }
    }
}
