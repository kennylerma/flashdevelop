﻿using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ASCompletion.Completion;
using ASCompletion.Context;
using ASCompletion.Model;
using PluginCore;
using PluginCore.Controls;
using ScintillaNet;

namespace HaXeContext.Completion
{
    class CodeComplete : ASComplete
    {
        protected override bool IsAvailable(IASContext ctx, bool autoHide)
        {
            return base.IsAvailable(ctx, autoHide) && (!autoHide || ((HaXeSettings)ctx.Settings).DisableCompletionOnDemand);
        }

        public override bool IsRegexStyle(ScintillaControl sci, int position)
        {
            var result = base.IsRegexStyle(sci, position);
            if (result) return true;
            return sci.BaseStyleAt(position) == 10 && sci.CharAt(position) == '~' && sci.CharAt(position + 1) == '/';
        }

        /// <summary>
        /// Returns whether or not position is inside of an expression block in String interpolation ('${expr}')
        /// </summary>
        public override bool IsStringInterpolationStyle(ScintillaControl sci, int position)
        {
            if (!ASContext.Context.Features.hasStringInterpolation) return false;
            var stringChar = sci.GetStringType(position - 1);
            if (ASContext.Context.Features.stringInterpolationQuotes.Contains(stringChar))
            {
                char current = (char)sci.CharAt(position);

                for (int i = position - 1; i >= 0; i--)
                {
                    var next = current;
                    current = (char)sci.CharAt(i);

                    if (current == stringChar)
                    {
                        if (!IsEscapedCharacter(sci, i)) break;
                    }
                    else if (current == '$')
                    {
                        if (next == '{' && !IsEscapedCharacter(sci, i, '$')) return true;
                    }
                    else if (current == '}')
                    {
                        i = sci.BraceMatch(i);
                        current = (char)sci.CharAt(i);
                        if (i > 0 && current == '{' && sci.CharAt(i - 1) == '$') break;
                    }
                }
            }
            return false;
        }

        /// <inheritdoc />
        protected override bool HandleWhiteSpaceCompletion(ScintillaControl sci, int position, string wordLeft, bool autoHide)
        {
            var currentClass = ASContext.Context.CurrentClass;
            if (currentClass.Flags.HasFlag(FlagType.Abstract))
            {
                switch (wordLeft)
                {
                    case "from":
                    case "to":
                        return PositionIsBeforeBody(sci, position, currentClass) && HandleNewCompletion(sci, string.Empty, autoHide, wordLeft);
                }
            }
            return base.HandleWhiteSpaceCompletion(sci, position, wordLeft, autoHide);
        }

        protected override void LocateMember(ScintillaControl sci, int line, string keyword, string name)
        {
            LocateMember(sci, line, $"{keyword ?? ""}\\s*(\\?)?(?<name>{name.Replace(".", "\\s*.\\s*")})[^A-z0-9]");
        }

        protected override void ParseLocalVars(ASExpr expression, FileModel model)
        {
            for (int i = 0, count = expression.ContextFunction.Parameters.Count; i < count; i++)
            {
                var item = expression.ContextFunction.Parameters[i];
                var name = item.Name;
                if (name[0] == '?')
                {
                    if (string.IsNullOrEmpty(item.Type) && (expression.Separator != "=" || item.Value != expression.Value))
                        InferParameterVarType(item);
                    var type = item.Type;
                    if (string.IsNullOrEmpty(type)) type = "Null<Dynamic>";
                    else if (!type.StartsWithOrdinal("Null<")) type = $"Null<{type}>";
                    item = (MemberModel) item.Clone();
                    item.Name = name.Substring(1);
                    item.Type = type;
                }
                model.Members.MergeByLine(item);
            }
        }

        /// <inheritdoc />
        protected override bool ResolveFunction(ScintillaControl sci, int position, ASResult expr, bool autoHide)
        {
            var member = expr.Member;
            if (member != null && (member.Flags & FlagType.Variable) != 0 &&
                !string.IsNullOrEmpty(member.Type) && member.Type.Contains("->"))
            {
                FunctionContextResolved(sci, expr.Context, member, expr.RelClass, false);
                return true;
            }
            var type = expr.Type;
            if ((expr.Member != null && expr.Path != "super") || !(type is ClassModel))
                return base.ResolveFunction(sci, position, expr, autoHide);
            var originConstructor = ASContext.GetLastStringToken(type.Name, ".");
            type.ResolveExtends();
            while (!type.IsVoid())
            {
                var constructor = type.Members.Search(ASContext.GetLastStringToken(type.Name, "."), FlagType.Constructor, 0);
                if (constructor != null)
                {
                    if (originConstructor != constructor.Name)
                    {
                        constructor = (MemberModel) constructor.Clone();
                        constructor.Name = originConstructor;
                    }
                    expr.Member = constructor;
                    expr.Context.Position = position;
                    FunctionContextResolved(sci, expr.Context, expr.Member, expr.RelClass, false);
                    return true;
                }
                if (type.Flags.HasFlag(FlagType.Abstract)) return false;
                type = type.Extends;
            }
            return false;
        }

        /// <inheritdoc />
        protected override void InferVariableType(ScintillaControl sci, ASExpr local, MemberModel var)
        {
            var line = sci.GetLine(var.LineFrom);
            var m = Regex.Match(line, "\\s*for\\s*\\(\\s*" + var.Name + "\\s*in\\s*");
            if (!m.Success)
            {
                base.InferVariableType(sci, local, var);
                return;
            }
            var ctx = ASContext.Context;
            var currentModel = ctx.CurrentModel;
            var rvalueStart = sci.PositionFromLine(var.LineFrom) + m.Index + m.Length;
            var methodEndPosition = sci.LineEndPosition(ctx.CurrentMember.LineTo);
            var parCount = 0;
            var braCount = 0;
            for (var i = rvalueStart; i < methodEndPosition; i++)
            {
                if (sci.PositionIsOnComment(i) || sci.PositionIsInString(i)) continue;
                var c = (char) sci.CharAt(i);
                if (c <= ' ') continue;
                if (c == '{') braCount++;
                else if (c == '}') braCount--;
                // for(i in 0...1)
                else if (c == '.' && sci.CharAt(i + 1) == '.' && sci.CharAt(i + 2) == '.')
                {
                    var type = ctx.ResolveType("Int", null);
                    var.Type = type.QualifiedName;
                    var.Flags |= FlagType.Inferred;
                    return;
                }
                if (c == '(') parCount++;
                // for(it in expr)
                else if (c == ')' || (c == ';' && braCount == 0))
                {
                    parCount--;
                    if (parCount >= 0) continue;
                    ASResult expr;
                    /**
                     * check:
                     * var a = [1,2,3,4];
                     * for(a in a)
                     * {
                     *     trace(a|); // | <-- cursor
                     * }
                     */
                    var wordLeft = sci.GetWordLeft(i - 1, false);
                    if (wordLeft == var.Name)
                    {
                        var lineBefore = sci.LineFromPosition(i) - 1;
                        var vars = local.LocalVars;
                        vars.Items.Sort((l, r) => l.LineFrom > r.LineFrom ? -1 : l.LineFrom < r.LineFrom ? 1 : 0);
                        var model = vars.Items.Find(it => it.LineFrom <= lineBefore);
                        if (model != null) expr = new ASResult {Type = ctx.ResolveType(model.Type, ctx.CurrentModel), InClass = ctx.CurrentClass};
                        // class members
                        else
                        {
                            expr = new ASResult();
                            FindMember(local.Value, ctx.CurrentClass, expr, 0, 0);
                            if (expr.IsNull()) return;
                        }
                    }
                    else expr = GetExpressionType(sci, i, false, true);
                    var exprType = expr.Type;
                    if (exprType == null) return;
                    string iteratorIndexType = null;
                    exprType.ResolveExtends();
                    while (!exprType.IsVoid())
                    {
                        // typedef Ints = Array<Int>
                        if (exprType.Flags.HasFlag(FlagType.TypeDef) && exprType.Members.Count == 0)
                        {
                            exprType = InferTypedefType(sci, exprType);
                            continue;
                        }
                        var members = exprType.Members;
                        var member = members.Search("iterator", 0, 0);
                        if (member == null)
                        {
                            if (members.Contains("hasNext", 0, 0))
                            {
                                member = members.Search("next", 0, 0);
                                if (member != null) iteratorIndexType = member.Type;
                            }
                            var exprTypeIndexType = exprType.IndexType;
                            if (exprType.Name.StartsWith("Iterator<") && !string.IsNullOrEmpty(exprTypeIndexType) && ctx.ResolveType(exprTypeIndexType, currentModel).IsVoid())
                            {
                                exprType = expr.InClass;
                                break;
                            }
                            if (iteratorIndexType != null) break;
                        }
                        else
                        {
                            var type = ctx.ResolveType(member.Type, currentModel);
                            iteratorIndexType = type.IndexType;
                            break;
                        }
                        exprType = exprType.Extends;
                    }
                    if (iteratorIndexType != null)
                    {
                        var.Type = iteratorIndexType;
                        var exprTypeIndexType = exprType.IndexType;
                        if (!string.IsNullOrEmpty(exprTypeIndexType) && exprTypeIndexType.Contains(','))
                        {
                            var t = exprType;
                            var originTypes = t.IndexType.Split(',');
                            if (!originTypes.Contains(var.Type))
                            {
                                var.Type = null;
                                t.ResolveExtends();
                                t = t.Extends;
                                while (!t.IsVoid())
                                {
                                    var types = t.IndexType.Split(',');
                                    for (var j = 0; j < types.Length; j++)
                                    {
                                        if (types[j] != iteratorIndexType) continue;
                                        var.Type = originTypes[j].Trim();
                                        break;
                                    }
                                    if (var.Type != null) break;
                                    t = t.Extends;
                                }
                            }
                        }
                    }
                    if (var.Type == null)
                    {
                        var type = ctx.ResolveType(ctx.Features.dynamicKey, null);
                        var.Type = type.QualifiedName;
                    }
                    var.Flags |= FlagType.Inferred;
                    return;
                }
            }
        }

        protected override void InferVariableType(ScintillaControl sci, string declarationLine, int rvalueStart, ASExpr local, MemberModel var)
        {
            if (local.PositionExpression <= rvalueStart && rvalueStart <= local.Position) return;
            var word = sci.GetWordRight(rvalueStart, true);
            // for example: var v = v;
            if (word == local.Value) return;
            var ctx = ASContext.Context;
            /**
             * for example:
             * class Foo {
             *   function new() {
             *     untyped __js__('value').<complete>
             *   }
             * }
             */
            if (word == "untyped")
            {
                var type = ctx.ResolveType(ctx.Features.dynamicKey, null);
                var.Type = type.QualifiedName;
                var.Flags |= FlagType.Inferred;
                return;
            }
            if (var.Flags.HasFlag(FlagType.LocalVar))
            {
                InferLocalVariableType(sci, declarationLine, rvalueStart, local, var);
                return;
            }
            if (var.Flags.HasFlag(FlagType.Variable))
            {
                var rvalueEnd = ExpressionEndPosition(sci, rvalueStart, true);
                var expr = GetExpressionType(sci, rvalueEnd, false, true);
                var type = expr.Type;
                if (type == null || type.IsVoid())
                {
                    if (expr.Member != null) type = ctx.ResolveType(expr.Member.Type, ctx.CurrentModel);
                    else
                    {
                        var token = sci.GetTextRange(rvalueStart, rvalueEnd);
                        type = ctx.ResolveToken(token, ctx.CurrentModel);
                    }
                }
                if (type.IsVoid()) type = ctx.ResolveType(ctx.Features.dynamicKey, null);
                var.Type = type.QualifiedName;
                var.Flags |= FlagType.Inferred;
            }
        }

        void InferLocalVariableType(ScintillaControl sci, string declarationLine, int rvalueStart, ASExpr local, MemberModel var)
        {
            var rvalueEnd = ExpressionEndPosition(sci, rvalueStart, sci.LineEndPosition(var.LineTo), true);
            var characterClass = ScintillaControl.Configuration.GetLanguage(sci.ConfigurationLanguage).characterclass.Characters;
            var methodEndPosition = sci.LineEndPosition(ASContext.Context.CurrentMember.LineTo);
            var arrCount = 0;
            var parCount = 0;
            var genCount = 0;
            var hadDot = false;
            var isInExpr = false;
            for (var i = rvalueEnd; i < methodEndPosition; i++)
            {
                if (arrCount == 0 && parCount == 0 && genCount == 0)
                {
                    if (sci.PositionIsOnComment(i)) continue;
                    if (sci.PositionIsInString(i))
                    {
                        if (isInExpr) break;
                        continue;
                    }
                }
                var c = (char) sci.CharAt(i);
                if (c == '[' && genCount == 0 && parCount == 0)
                {
                    arrCount++;
                    isInExpr = true;
                }
                else if (c == ']' && genCount == 0 && parCount == 0)
                {
                    arrCount--;
                    rvalueEnd = i + 1;
                    if (arrCount < 0) break;
                }
                else if (c == '(' && genCount == 0 && arrCount == 0)
                {
                    parCount++;
                    isInExpr = true;
                }
                else if (c == ')' && genCount == 0 && arrCount == 0)
                {
                    parCount--;
                    rvalueEnd = i + 1;
                    if (parCount < 0) break;
                }
                else if (c == '<' && parCount == 0 && arrCount == 0)
                {
                    genCount++;
                    isInExpr = true;
                }
                else if (c == '>' && parCount == 0 && arrCount == 0)
                {
                    genCount--;
                    rvalueEnd = i + 1;
                    if (genCount < 0) break;
                }
                if (parCount > 0 || genCount > 0 || arrCount > 0) continue;
                if (c <= ' ')
                {
                    hadDot = false;
                    isInExpr = true;
                    continue;
                }
                if (c == ';' || (!hadDot && characterClass.Contains(c))) break;
                if (c == '.')
                {
                    hadDot = true;
                    rvalueEnd = ExpressionEndPosition(sci, i + 1, methodEndPosition);
                }
                isInExpr = true;
            }
            var expr = GetExpressionType(sci, rvalueEnd, false, true);
            if (expr.Type != null)
            {
                var.Type = expr.Type.QualifiedName;
                var.Flags |= FlagType.Inferred;
                return;
            }
            if (expr.Member != null)
            {
                var.Type = expr.Member.Type;
                var.Flags |= FlagType.Inferred;
                return;
            }
            base.InferVariableType(sci, declarationLine, rvalueStart, local, var);
        }

        static ClassModel InferTypedefType(ScintillaControl sci, MemberModel expr)
        {
            var text = sci.GetLine(expr.LineFrom);
            var m = Regex.Match(text, "\\s*typedef\\s+" + expr.Name + "\\s*=([^;]+)");
            if (!m.Success) return ClassModel.VoidClass;
            var rvalue = m.Groups[1].Value.TrimStart();
            return ASContext.Context.ResolveType(rvalue, ASContext.Context.CurrentModel);
        }

        /// <inheritdoc />
        protected override bool HandleImplementsCompletion(ScintillaControl sci, bool autoHide)
        {
            var extends = new HashSet<string>();
            var list = new List<ICompletionListItem>();
            foreach (var it in ASContext.Context.GetAllProjectClasses().Items.Distinct())
            {
                extends.Clear();
                var type = it as ClassModel ?? ClassModel.VoidClass;
                type.ResolveExtends();
                while (!type.IsVoid() && type.Flags.HasFlag(FlagType.TypeDef) && type.Members.Count == 0)
                {
                    if (extends.Contains(type.Type)) break;
                    extends.Add(type.Type);
                    if (!string.IsNullOrEmpty(type.ExtendsType))
                    {
                        type = type.Extends;
                        if (extends.Contains(type.ExtendsType)) break;
                    }
                    else type = InferTypedefType(sci, type);
                }
                if (!type.Flags.HasFlag(FlagType.Interface)) continue;
                list.Add(new MemberItem(it));
            }
            CompletionList.Show(list, autoHide);
            return true;
        }

        protected override ASResult EvalExpression(string expression, ASExpr context, FileModel inFile, ClassModel inClass, bool complete, bool asFunction, bool filterVisibility)
        {
            if (!string.IsNullOrEmpty(expression))
            {
                var ctx = ASContext.Context;
                var features = ctx.Features;
                if (context.SubExpressions != null)
                {
                    var count = context.SubExpressions.Count - 1;
                    // transform #2~.#1~.#0~ to #2~.[].[]
                    for (var i = 0; i <= count; i++)
                    {
                        var subExpression = context.SubExpressions[i];
                        if (subExpression.Length < 2 || subExpression[0] != '[') continue;
                        // for example: [].<complete>, [1 => 2].<complete>
                        if (expression[0] == '#' && i == count)
                        {
                            var type = ctx.ResolveToken(subExpression, inFile);
                            if (type.IsVoid()) break;
                            expression = type.Name + ".#" + expression.Substring(("#" + i + "~").Length);
                            context.SubExpressions.RemoveAt(i);
                            return base.EvalExpression(expression, context, inFile, inClass, complete, asFunction, filterVisibility);
                        }
                        expression = expression.Replace(".#" + i + "~", "." + subExpression);
                    }
                }
                var c = expression[0];
                if (c == '\'' || c == '"')
                {
                    var type = ctx.ResolveType(features.stringKey, inFile);
                    // for example: ""|, ''|
                    if (context.SubExpressions == null) expression = type.Name + ".#.";
                    // for example: "".<complete>, ''.<complete>
                    else
                    {
                        var pattern = c + ".#" + (context.SubExpressions.Count - 1) + "~";
                        var startIndex = expression.IndexOfOrdinal(pattern) + pattern.Length;
                        expression = type.Name + ".#" + expression.Substring(startIndex);
                        if (context.SubExpressions.Count == 1) context.SubExpressions = null;
                    }
                }
                // for example: ~/pattern/.<complete>
                else if (expression.StartsWithOrdinal("#RegExp")) expression = expression.Replace("#RegExp", "EReg");
                else if (context.SubExpressions != null && context.SubExpressions.Count > 0)
                {
                    var lastIndex = context.SubExpressions.Count - 1;
                    var pattern = "#" + lastIndex + "~";
                    // for example: cast(v, T).<complete>, (v is T).<complete>, (v:T).<complete>, ...
                    if (expression.StartsWithOrdinal(pattern))
                    {
                        var expr = context.SubExpressions[lastIndex];
                        if (context.WordBefore == "cast") expr = "cast" + expr;
                        var type = ctx.ResolveToken(expr, inFile);
                        if (!type.IsVoid()) expression = type.Name + ".#" + expression.Substring(pattern.Length);
                    }
                }
                /**
                 * for example:
                 * macro function foo(v:Expr):Expr {
                 *     return macro {
                 *         $v.<complete>
                 *     }
                 * }
                 */
                if (string.IsNullOrEmpty(context.WordBefore) && context.PositionExpression > 0 &&
                    ASContext.CurSciControl != null && ASContext.CurSciControl.CharAt(context.PositionExpression - 1) == '$')
                {
                    context.PositionExpression -= 1;
                    context.Value = $"${context.Value}";
                }
            }
            return base.EvalExpression(expression, context, inFile, inClass, complete, asFunction, filterVisibility);
        }

        protected override string GetConstructorTooltipText(ClassModel type)
        {
            var inClass = type;
            type.ResolveExtends();
            while (!type.IsVoid())
            {
                var member = type.Members.Search(type.Name, FlagType.Constructor, 0);
                if (member != null)
                {
                    if (member.Name != inClass.Name)
                    {
                        member = (MemberModel) member.Clone();
                        member.Name = inClass.Name;
                        inClass = type;
                    }
                    return MemberTooltipText(member, inClass) + GetToolTipDoc(member);
                }
                type = type.Extends;
            }
            return null;
        }

        public override MemberModel FunctionTypeToMemberModel(string type, FileModel inFile)
        {
            var voidKey = ASContext.Context.Features.voidKey;
            if (type == "Function")
            {
                var paramType = ASContext.Context.ResolveType(type, inFile);
                if (paramType.InFile.Package == "haxe" && paramType.InFile.Module == "Constraints")
                    return new MemberModel {Type = voidKey};
            }
            var result = new MemberModel {Parameters = new List<MemberModel>()};
            var parCount = 0;
            var braCount = 0;
            var genCount = 0;
            var startPosition = 0;
            var typeLength = type.Length;
            for (var i = 0; i < typeLength; i++)
            {
                string parameterType = null;
                var c = type[i];
                if (c == '(') parCount++;
                else if (c == ')')
                {
                    parCount--;
                    if (parCount == 0 && braCount == 0 && genCount == 0)
                    {
                        parameterType = type.Substring(startPosition, (i + 1) - startPosition);
                        startPosition = i + 1;
                    }
                }
                else if (c == '{') braCount++;
                else if (c == '}')
                {
                    braCount--;
                    if (parCount == 0 && braCount == 0 && genCount == 0)
                    {
                        parameterType = type.Substring(startPosition, (i + 1) - startPosition);
                        startPosition = i + 1;
                    }
                }
                else if (c == '<') genCount++;
                else if (c == '>' && type[i - 1] != '-')
                {
                    genCount--;
                    if (parCount == 0 && braCount == 0 && genCount == 0)
                    {
                        parameterType = type.Substring(startPosition, (i + 1) - startPosition);
                        startPosition = i + 1;
                    }
                }
                else if (parCount == 0 && braCount == 0 && genCount == 0 && c == '-' && type[i + 1] == '>')
                {
                    if (i > startPosition) parameterType = type.Substring(startPosition, i - startPosition);
                    startPosition = i + 2;
                    i++;
                }
                if (parameterType == null)
                {
                    if (i == typeLength - 1 && i > startPosition) result.Type = type.Substring(startPosition);
                    continue;
                }
                var parameterName = $"parameter{result.Parameters.Count}";
                if (parameterType.StartsWith('?'))
                {
                    parameterName = $"?{parameterName}";
                    parameterType = parameterType.TrimStart('?');
                }
                if (i == typeLength - 1) result.Type = parameterType;
                else result.Parameters.Add(new MemberModel(parameterName, parameterType, FlagType.ParameterVar, 0));
            }
            if (result.Parameters.Count == 1 && result.Parameters[0].Type == voidKey)
                result.Parameters.Clear();
            return result;
        }

        protected override string GetCalltipDef(MemberModel member)
        {
            if ((member.Flags & FlagType.Variable) != 0 && !string.IsNullOrEmpty(member.Type) && member.Type.Contains("->"))
            {
                var tmp = FunctionTypeToMemberModel(member.Type, member.InFile);
                tmp.Name = member.Name;
                tmp.Flags |= FlagType.Function;
                member = tmp;
            }
            return base.GetCalltipDef(member);
        }
    }
}