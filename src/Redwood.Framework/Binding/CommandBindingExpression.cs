﻿using System;
using Redwood.Framework.Controls;
using Redwood.Framework.Parser.Translation;

namespace Redwood.Framework.Binding
{
    public class CommandBindingExpression : BindingExpression
    {

        private static ExpressionTranslator translator = new ExpressionTranslator();


        public CommandBindingExpression()
        {
        }

        public CommandBindingExpression(string expression)
            : base(expression)
        {
        }


        /// <summary>
        /// Evaluates the binding.
        /// </summary>
        public override object Evaluate(RedwoodBindableControl control, RedwoodProperty property)
        {
            // TODO: implement server evaluation
            throw new NotImplementedException();
        }

        /// <summary>
        /// Translates the binding to client script.
        /// </summary>
        public override string TranslateToClientScript()
        {
            return translator.Translate(Expression);
        }
    }
}