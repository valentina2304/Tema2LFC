﻿using Antlr4.Runtime.Misc;
using System;
using System.Collections.Generic;
using System.Linq;

public class CompilerVisitor : BasicLanguageBaseVisitor<CompilerSymbols>
{
    private CompilerSymbols _symbols = new();
    private CompilerSymbols.Function? _currentFunction;
    private Stack<CompilerSymbols.ControlStructure> _controlStructureStack = new();

    public override CompilerSymbols VisitProgram([NotNull] BasicLanguageParser.ProgramContext context)
    {
        foreach (var child in context.children)
        {
            Visit(child);
        }
        ValidateProgram();
        return _symbols;
    }

    public override CompilerSymbols VisitGlobalVariableDecl([NotNull] BasicLanguageParser.GlobalVariableDeclContext context)
    {
        var variable = CreateVariable(context.type(), context.IDENTIFIER().GetText(), true, context.Start.Line);

        if (_symbols.GlobalVariables.Any(v => v.Name == variable.Name))
        {
            AddError(CompilerError.ErrorType.Semantic, context.Start.Line,
                    $"Duplicate global variable declaration: {variable.Name}");
            return _symbols;
        }

        if (context.expr() != null)
        {
            ValidateInitialValue(variable, context.expr());
        }

        _symbols.GlobalVariables.Add(variable);
        return _symbols;
    }

    public override CompilerSymbols VisitFunctionDecl([NotNull] BasicLanguageParser.FunctionDeclContext context)
    {
        var functionName = context.MAIN_FUN()?.GetText() ?? context.IDENTIFIER().GetText();
        var function = new CompilerSymbols.Function
        {
            Name = functionName,
            DeclarationLine = context.Start.Line,
            ReturnType = ParseType(context.type().GetText())
        };

        if (_symbols.Functions.Any(f => f.Name == function.Name))
        {
            AddError(CompilerError.ErrorType.Semantic, context.Start.Line,
                    $"Duplicate function declaration: {function.Name}");
            return _symbols;
        }

        _currentFunction = function;

        if (context.paramList() != null)
        {
            Visit(context.paramList());
        }

        Visit(context.block());

        DetermineFunctionType(function, context);

        _symbols.Functions.Add(function);
        _currentFunction = null;

        return _symbols;
    }

    public override CompilerSymbols VisitParamList([NotNull] BasicLanguageParser.ParamListContext context)
    {
        foreach (var param in context.param())
        {
            var parameter = CreateVariable(param.type(), param.IDENTIFIER().GetText(), false, param.Start.Line);

            if (_currentFunction.Parameters.Any(p => p.Name == parameter.Name))
            {
                AddError(CompilerError.ErrorType.Semantic, param.Start.Line,
                        $"Duplicate parameter name in function {_currentFunction.Name}: {parameter.Name}");
                continue;
            }

            _currentFunction.Parameters.Add(parameter);
        }
        return _symbols;
    }

    public override CompilerSymbols VisitVariableDecl([NotNull] BasicLanguageParser.VariableDeclContext context)
    {
        if (_currentFunction == null)
        {
            AddError(CompilerError.ErrorType.Semantic, context.Start.Line,
                    "Variable declaration outside of function context");
            return _symbols;
        }

        var variable = CreateVariable(context.type(), context.IDENTIFIER().GetText(), false, context.Start.Line);

        if (_currentFunction.LocalVariables.Any(v => v.Name == variable.Name))
        {
            AddError(CompilerError.ErrorType.Semantic, context.Start.Line,
                    $"Duplicate local variable declaration: {variable.Name}");
            return _symbols;
        }

        if (context.expr() != null)
        {
            ValidateInitialValue(variable, context.expr());
        }

        _currentFunction.LocalVariables.Add(variable);
        return _symbols;
    }

    public override CompilerSymbols VisitIfStatement([NotNull] BasicLanguageParser.IfStatementContext context)
    {
        var structure = new CompilerSymbols.ControlStructure
        {
            Type = context.ELSE() != null ?
                   CompilerSymbols.ControlStructure.StructureType.IfElse :
                   CompilerSymbols.ControlStructure.StructureType.If,
            StartLine = context.Start.Line,
            EndLine = context.Stop.Line,
            Condition = context.expr().GetText()
        };

        _controlStructureStack.Push(structure);
        _currentFunction?.ControlStructures.Add(structure);

        Visit(context.block(0));
        if (context.ELSE() != null)
        {
            Visit(context.block(1));
        }

        _controlStructureStack.Pop();
        return _symbols;
    }

    public override CompilerSymbols VisitWhileStatement([NotNull] BasicLanguageParser.WhileStatementContext context)
    {
        var structure = new CompilerSymbols.ControlStructure
        {
            Type = CompilerSymbols.ControlStructure.StructureType.While,
            StartLine = context.Start.Line,
            EndLine = context.Stop.Line,
            Condition = context.expr().GetText()
        };

        _controlStructureStack.Push(structure);
        _currentFunction?.ControlStructures.Add(structure);

        Visit(context.block());

        _controlStructureStack.Pop();
        return _symbols;
    }

    public override CompilerSymbols VisitForStatement([NotNull] BasicLanguageParser.ForStatementContext context)
    {
        var structure = new CompilerSymbols.ControlStructure
        {
            Type = CompilerSymbols.ControlStructure.StructureType.For,
            StartLine = context.Start.Line,
            EndLine = context.Stop.Line,
            Condition = context.expr().FirstOrDefault()?.GetText() ?? ""
        };

        _controlStructureStack.Push(structure);
        _currentFunction?.ControlStructures.Add(structure);

        if (context.variableDecl() != null)
            Visit(context.variableDecl());
        else if (context.exprStatement() != null)
            Visit(context.exprStatement());

        Visit(context.block());

        _controlStructureStack.Pop();
        return _symbols;
    }

    public override CompilerSymbols VisitExpr([NotNull] BasicLanguageParser.ExprContext context)
    {
        if (context.assignmentExpr() != null)
        {
            var varName = context.assignmentExpr().IDENTIFIER().GetText();
            if (!IsVariableDeclared(varName))
            {
                AddError(CompilerError.ErrorType.Semantic, context.Start.Line,
                        $"Assignment to undeclared variable: {varName}");
            }
        }
        else if (context.logicalExpr() != null)
        {
            ValidateExpression(context.logicalExpr());
        }

        return base.VisitExpr(context);
    }
    private bool IsFunctionDeclared(string name)
    {
        return _symbols.Functions.Any(f => f.Name == name);
    }

    public override CompilerSymbols VisitPrimaryExpr([NotNull] BasicLanguageParser.PrimaryExprContext context)
    {
        if (context.IDENTIFIER() != null)
        {
            var name = context.IDENTIFIER().GetText();

           
            if (context.LPAREN() != null)  
            {
                if (!IsFunctionDeclared(name))
                {
                    AddError(CompilerError.ErrorType.Semantic, context.Start.Line,
                            $"Call to undefined function: {name}");
                }
                else
                {
                    var function = _symbols.Functions.First(f => f.Name == name);
                    var expectedArgs = function.Parameters.Count;
                    var providedArgs = context.exprList()?.expr().Length ?? 0;

                    if (expectedArgs != providedArgs)
                    {
                        AddError(CompilerError.ErrorType.Semantic, context.Start.Line,
                                $"Function {name} expects {expectedArgs} arguments but got {providedArgs}");
                    }
                }
            }
    
            else if (!IsVariableDeclared(name) && !IsFunctionDeclared(name))
            {
                AddError(CompilerError.ErrorType.Semantic, context.Start.Line,
                        $"Use of undeclared variable: {name}");
            }
        }

        return base.VisitPrimaryExpr(context);
    }
    private CompilerSymbols.Variable CreateVariable(BasicLanguageParser.TypeContext typeContext, string name, bool isGlobal, int line)
    {
        return new CompilerSymbols.Variable
        {
            Name = name,
            VariableType = ParseType(typeContext.GetText()),
            IsGlobal = isGlobal,
            DeclarationLine = line
        };
    }

    private CompilerSymbols.Variable.Type ParseType(string typeText)
    {
        return typeText.ToLower() switch
        {
            "int" => CompilerSymbols.Variable.Type.Int,
            "float" => CompilerSymbols.Variable.Type.Float,
            "double" => CompilerSymbols.Variable.Type.Double,
            "string" => CompilerSymbols.Variable.Type.String,
            "void" => CompilerSymbols.Variable.Type.Void,
            _ => throw new Exception($"Unknown type {typeText}")
        };
    }

    private void ValidateInitialValue(CompilerSymbols.Variable variable, BasicLanguageParser.ExprContext expression)
    {
    
        if (expression.GetText().Contains("+") ||
            expression.GetText().Contains("-") ||
            expression.GetText().Contains("*") ||
            expression.GetText().Contains("/") ||
            expression.GetText().Contains("("))
        {
            ValidateExpression(expression.logicalExpr());
            return;
        }

        var value = expression.GetText();

        try
        {
            switch (variable.VariableType)
            {
                case CompilerSymbols.Variable.Type.Int:
                    if (int.TryParse(value, out int intValue))
                        variable.InitialValue = intValue;
                    break;
                case CompilerSymbols.Variable.Type.Float:
                case CompilerSymbols.Variable.Type.Double:
                    if (double.TryParse(value, out double doubleValue))
                        variable.InitialValue = doubleValue;
                    break;
                case CompilerSymbols.Variable.Type.String:
                    if (value.StartsWith("\"") && value.EndsWith("\""))
                        variable.InitialValue = value.Trim('"');
                    break;
            }
        }
        catch (Exception ex)
        {
            AddError(CompilerError.ErrorType.Semantic, expression.Start.Line,
                    $"Invalid value for type {variable.VariableType}: {value}");
        }
    }
    private void DetermineFunctionType(CompilerSymbols.Function function, BasicLanguageParser.FunctionDeclContext context)
    {
        if (function.Name.ToLower() == "main")
        {
            function.Type = CompilerSymbols.Function.FunctionType.Main;
        }
        else
        {
            var functionCalls = context.block()
                .GetRuleContexts<BasicLanguageParser.PrimaryExprContext>()
                .Count(fc => fc.IDENTIFIER()?.GetText() == function.Name);

            function.Type = functionCalls > 0 ?
                CompilerSymbols.Function.FunctionType.Recursive :
                CompilerSymbols.Function.FunctionType.Normal;
        }
    }

    private void ValidateExpression(BasicLanguageParser.LogicalExprContext context)
    {
        if (context == null) return;

        foreach (var identifier in context.GetTokens(BasicLanguageParser.IDENTIFIER))
        {
            var name = identifier.GetText();

            bool isFunction = _symbols.Functions.Any(f => f.Name == name);

            if (!isFunction && !IsVariableDeclared(name))
            {
                AddError(CompilerError.ErrorType.Semantic, context.Start.Line,
                        $"Use of undeclared variable: {name}");
            }
        }
    }


    private void ValidateProgram()
    {
        if (!_symbols.Functions.Any(f => f.Name.ToLower() == "main"))
        {
            AddError(CompilerError.ErrorType.Semantic, 0, "No main function found in program");
        }
    }

    private bool IsVariableDeclared(string name)
    {
        return _symbols.GlobalVariables.Any(v => v.Name == name) ||
               (_currentFunction?.LocalVariables.Any(v => v.Name == name) ?? false) ||
               (_currentFunction?.Parameters.Any(p => p.Name == name) ?? false);
    }

    private void AddError(CompilerError.ErrorType type, int line, string message)
    {
        _symbols.Errors.Add(new CompilerError
        {
            Type = type,
            Line = line,
            Message = message
        });
    }
}