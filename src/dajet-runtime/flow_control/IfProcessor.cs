﻿using DaJet.Scripting.Model;

namespace DaJet.Runtime
{
    public sealed class IfProcessor : IProcessor
    {
        private IProcessor _next;
        private IProcessor _next_then;
        private IProcessor _next_else;
        private readonly ScriptScope _scope;
        private readonly IfStatement _statement;
        public IfProcessor(in ScriptScope scope)
        {
            _scope = scope ?? throw new ArgumentNullException(nameof(scope));

            if (_scope.Owner is not IfStatement statement)
            {
                throw new ArgumentException(nameof(IfStatement));
            }

            _statement = statement;

            ScriptScope then_scope = _scope.Create(_statement.THEN);
            _next_then = StreamFactory.CreateStream(in then_scope);

            if (_statement.ELSE is not null)
            {
                ScriptScope else_scope = _scope.Create(_statement.ELSE);
                _next_else = StreamFactory.CreateStream(in else_scope);
            }
        }
        public void Dispose()
        {
            _next_then?.Dispose();
            _next_else?.Dispose();
            _next?.Dispose();
        }
        public void Synchronize()
        {
            _next_then?.Synchronize();
            _next_else?.Synchronize();
            _next?.Synchronize();
        }
        public void LinkTo(in IProcessor next) { _next = next; }
        public void Process()
        {
            if (ConditionIsTrue())
            {
                _next_then?.Process();
            }
            else
            {
                _next_else?.Process();
            }

            _next?.Process();
        }
        private bool ConditionIsTrue()
        {
            if (_statement.IF is null) { return true; }

            SyntaxNode expression = _statement.IF;

            return StreamFactory.Evaluate(in _scope, in expression);
        }
    }
}