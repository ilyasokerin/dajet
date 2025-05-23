﻿using DaJet.Data;
using DaJet.Metadata.Model;
using DaJet.Scripting.Model;
using System.Text;

namespace DaJet.Scripting
{
    public sealed class MsSqlTranspiler : SqlTranspiler
    {
        private string GetCreateTableColumnList(in SelectExpression select)
        {
            StringBuilder columns = new();

            ColumnMapper column;
            PropertyMapper property;
            EntityMapper map = DataMapper.CreateEntityMap(in select);

            for (int i = 0; i < map.Properties.Count; i++)
            {
                property = map.Properties[i];

                for (int ii = 0; ii < property.ColumnSequence.Count; ii++)
                {
                    column = property.ColumnSequence[ii];

                    if (column.Ordinal > 0) { columns.Append(", "); }

                    columns.Append(column.Alias).Append(' ').Append(column.TypeName);
                }
            }

            return columns.ToString();
        }
        protected override void Visit(in TableReference node, in StringBuilder script)
        {
            if (node.Binding is ApplicationObject entity)
            {
                script.Append(entity.TableName);
            }
            else if (node.Binding is TableExpression || node.Binding is CommonTableExpression)
            {
                script.Append(node.Identifier);
            }
            else if (node.Binding is TableVariableExpression)
            {
                script.Append($"@{node.Identifier}");
            }
            else if (node.Binding is TemporaryTableExpression)
            {
                script.Append($"#{node.Identifier}");
            }

            if (!string.IsNullOrEmpty(node.Alias))
            {
                script.Append(" AS ").Append(node.Alias);
            }

            if (!string.IsNullOrEmpty(node.Hints))
            {
                script.Append(' ').Append(node.Hints); // CONSUME statement support only
            }
        }
        protected override void Visit(in TableVariableExpression node, in StringBuilder script)
        {
            SelectExpression source = DataMapper.GetColumnSource(node.Expression) as SelectExpression;

            if (source is null) { return; }

            script.Append($"DECLARE @{node.Name} TABLE (");
            script.Append(GetCreateTableColumnList(in source));
            script.Append(");").AppendLine();
            script.Append($"INSERT @{node.Name}").AppendLine();

            base.Visit(in node, in script);
        }
        protected override void Visit(in TemporaryTableExpression node, in StringBuilder script)
        {
            SelectExpression source = DataMapper.GetColumnSource(node.Expression) as SelectExpression;

            if (source is null) { return; }

            script.Append($"CREATE TABLE #{node.Name} (");
            script.Append(GetCreateTableColumnList(in source));
            script.Append(");").AppendLine();
            script.Append($"INSERT #{node.Name}").AppendLine();

            base.Visit(in node, in script);
        }
        protected override void VisitTargetTable(in TableReference node, in StringBuilder script)
        {
            if (node.Binding is ApplicationObject entity)
            {
                script.Append(entity.TableName);
            }
            else if (node.Binding is TableExpression || node.Binding is CommonTableExpression)
            {
                script.Append(node.Identifier);
            }
            else if (node.Binding is TableVariableExpression)
            {
                script.Append($"@{node.Identifier}");
            }
            else if (node.Binding is TemporaryTableExpression)
            {
                script.Append($"#{node.Identifier}");
            }
            else
            {
                throw new InvalidOperationException("MS-DML: Target table identifier is missing.");
            }
        }

        protected override void Visit(in FunctionExpression node, in StringBuilder script)
        {
            string name = node.Name.ToUpperInvariant();

            if (UDF.TryGet(node.Name, out IUserDefinedFunction transpiler))
            {
                FunctionDescriptor function = transpiler.Transpile(this, in node, in script);

                if (function is not null)
                {
                    Functions.Add(function);
                }
            }
            else if (name == "NOW")
            {
                if (YearOffset == 0)
                {
                    script.Append("GETDATE()");
                }
                else
                {
                    script.Append("DATEADD(year, " + YearOffset.ToString() + ", GETDATE())");
                }
            }
            else if (name == "UTC")
            {
                if (YearOffset == 0)
                {
                    script.Append("GETUTCDATE()");
                }
                else
                {
                    script.Append("DATEADD(year, " + YearOffset.ToString() + ", GETUTCDATE())");
                }
            }
            else if (name == "VECTOR")
            {
                if (node.Parameters is not null && node.Parameters.Count > 0 && node.Parameters[0] is ScalarExpression scalar)
                {
                    script.Append("NEXT VALUE FOR ").Append(scalar.Literal);
                }
            }
            else if (name == "CHARLENGTH")
            {
                script.Append("LEN").Append('(');
                Visit(node.Parameters[0], in script);
                script.Append(')');
            }
            else if (name == "NEWUUID")
            {
                script.Append("NEWID()");
            }
            else if (node.Token != TokenType.UDF)
            {
                base.Visit(in node, in script);
            }
            else
            {
                throw new InvalidOperationException($"Invalid function name: {node.Name}");
            }
        }

        protected override void Visit(in UpsertStatement node, in StringBuilder script)
        {
            if (node.Target.Binding is MetadataObject || node.Target.Binding is TemporaryTableExpression)
            {
                node.Hints = new() { "UPDLOCK", "SERIALIZABLE" };
            }

            base.Visit(in node, in script);
        }

        #region "UPDATE STATEMENT"
        protected override void Visit(in UpdateStatement node, in StringBuilder script)
        {
            if (node.Output is not null)
            {
                foreach (ColumnExpression column in node.Output.Columns)
                {
                    if (column.Expression is ColumnReference reference)
                    {
                        ParserHelper.GetColumnIdentifiers(reference.Identifier, out string tableAlias, out _);

                        if (string.IsNullOrEmpty(tableAlias))
                        {
                            reference.Identifier = "inserted." + reference.Identifier;

                            if (reference.Mapping is not null)
                            {
                                foreach (ColumnMapper map in reference.Mapping)
                                {
                                    map.Name = "inserted." + map.Name;
                                }
                            }
                        }
                    }
                }
            }

            base.Visit(in node, in script);
        }
        #endregion

        #region "DELETE STATEMENT"
        protected override void Visit(in DeleteStatement node, in StringBuilder script)
        {
            if (node.CommonTables is not null)
            {
                script.Append("WITH ");

                Visit(node.CommonTables, in script);
            }

            script.Append("DELETE ");

            VisitTargetTable(node.Target, in script);

            if (node.Output is not null)
            {
                foreach (ColumnExpression column in node.Output.Columns)
                {
                    if (column.Expression is ColumnReference reference)
                    {
                        ParserHelper.GetColumnIdentifiers(reference.Identifier, out string tableAlias, out _);

                        if (string.IsNullOrEmpty(tableAlias))
                        {
                            reference.Identifier = "deleted." + reference.Identifier;

                            if (reference.Mapping is not null)
                            {
                                foreach (ColumnMapper map in reference.Mapping)
                                {
                                    map.Name = "deleted." + map.Name;
                                }
                            }
                        }
                    }
                }

                Visit(node.Output, script);
            }

            if (node.From is not null)
            {
                Visit(node.From, script);
            }

            if (node.Where is not null)
            {
                Visit(node.Where, script);
            }
        }
        protected override void Visit(in OutputClause node, in StringBuilder script)
        {
            script.AppendLine().AppendLine("OUTPUT");

            for (int i = 0; i < node.Columns.Count; i++)
            {
                if (i > 0) { script.AppendLine(","); }

                Visit(node.Columns[i], in script);
            }

            if (node.Into is not null) { Visit(node.Into, in script); }
        }
        #endregion

        #region "CONSUME STATEMENT"
        protected override void Visit(in ConsumeStatement node, in StringBuilder script)
        {
            if (!string.IsNullOrEmpty(node.Target))
            {
                return; //NOTE: stream processor statement
            }

            if (!TryGetFromTable(node.From, out TableReference table))
            {
                throw new InvalidOperationException("CONSUME: target table is not found.");
            }

            if (node.Into is IntoClause into)
            {
                if (into.Table is not null)
                {
                    CreateTypeStatement statement = CreateTypeDefinition(node.Into.Table.Identifier, node.Columns);

                    script.Insert(0, ScriptDeclareTableVariableStatement(in statement));
                }
            }

            DeleteStatement output;

            if (node.From.Expression is TableReference)
            {
                output = TransformSimpleConsume(in node, in table);
            }
            else
            {
                output = TransformComplexConsume(in node, in table);
            }
            
            Visit(in output, in script);
        }
        private IndexInfo GetPrimaryOrUniqueIndex(in string tableName)
        {
            List<IndexInfo> indexes = new MsSqlHelper().GetIndexes(Metadata.ConnectionString, tableName);

            foreach (IndexInfo index in indexes)
            {
                if (index.IsPrimary) { return index; }
            }

            foreach (IndexInfo index in indexes)
            {
                if (index.IsUnique && index.IsClustered) { return index; }
            }

            foreach (IndexInfo index in indexes)
            {
                if (index.IsUnique) { return index; }
            }

            return null;
        }
        private IndexInfo GetPrimaryOrUniqueIndex(in TableReference table)
        {
            if (table.Binding is not ApplicationObject entity)
            {
                throw new InvalidOperationException("CONSUME: target table has no entity binding.");
            }

            string target = entity.TableName.ToLowerInvariant();

            List<IndexInfo> indexes = new MsSqlHelper().GetIndexes(Metadata.ConnectionString, target);

            foreach (IndexInfo index in indexes)
            {
                if (index.IsPrimary) { return index; }
            }

            foreach (IndexInfo index in indexes)
            {
                if (index.IsUnique && index.IsClustered) { return index; }
            }

            foreach (IndexInfo index in indexes)
            {
                if (index.IsUnique) { return index; }
            }

            return null;
        }
        private string ScriptDeclareTableVariableStatement(in CreateTypeStatement statement)
        {
            StringBuilder script = new();
            
            script.Append("DECLARE @").Append(statement.Identifier).AppendLine(" AS TABLE (");

            for (int i = 0; i < statement.Columns.Count; i++)
            {
                ColumnDefinition column = statement.Columns[i];

                if (i > 0) { script.AppendLine(","); }

                script.Append(column.Name).Append(' ').Append(column.Type.Identifier);
            }
            
            script.AppendLine(");");

            return script.ToString();
        }

        #region "CONSUME FROM ONE TARGET TABLE"
        private DeleteStatement TransformSimpleConsume(in ConsumeStatement node, in TableReference table)
        {
            SelectExpression source = TransformConsumeToSelect(in node, in table);

            CommonTableExpression queue = new() { Name = "queue", Expression = source };

            return TransformConsumeToDelete(in node, in queue);
        }
        private SelectExpression TransformConsumeToSelect(in ConsumeStatement consume, in TableReference target)
        {
            target.Hints = "WITH (ROWLOCK" + (consume.StrictOrderRequired ? ")" : ", READPAST)");

            SelectExpression select = new()
            {
                Columns = consume.Columns,
                Top = consume.Top,
                From = consume.From,
                Where = consume.Where,
                Order = consume.Order
            };

            return select;
        }
        private DeleteStatement TransformConsumeToDelete(in ConsumeStatement consume, in CommonTableExpression queue)
        {
            DeleteStatement delete = new()
            {
                Output = new OutputClause()
                {
                    Into = consume.Into
                },
                Target = new TableReference()
                {
                    Binding = queue,
                    Identifier = queue.Name
                },
                CommonTables = queue
            };

            foreach (ColumnExpression output in consume.Columns)
            {
                if (output.Expression is ColumnReference column)
                {
                    ColumnExpression expression = new() { Alias = output.Alias };

                    ParserHelper.GetColumnIdentifiers(column.Identifier, out _, out string columnName);

                    ColumnReference reference = new()
                    {
                        Binding = output,
                        Identifier = "deleted." + (string.IsNullOrEmpty(output.Alias) ? columnName : output.Alias)
                    };

                    if (column.Mapping is not null)
                    {
                        reference.Mapping = new List<ColumnMapper>();

                        foreach (ColumnMapper map in column.Mapping)
                        {
                            reference.Mapping.Add(new ColumnMapper()
                            {
                                Type = map.Type,
                                Name = "deleted." + map.Alias
                            });
                        }
                    }

                    expression.Expression = reference;

                    delete.Output.Columns.Add(expression);
                }
                else if (output.Expression is FunctionExpression function && function.Token == TokenType.DATALENGTH)
                {
                    delete.Output.Columns.Add(new ColumnExpression()
                    {
                        Expression = new ColumnReference()
                        {
                            Binding = output,
                            Identifier = "deleted." + output.Alias,
                            Mapping = new List<ColumnMapper>()
                            {
                                new ColumnMapper()
                                {
                                    Type = UnionTag.Integer,
                                    Name ="deleted." + output.Alias
                                }
                            }
                        }
                    });
                }
            }

            return delete;
        }
        #endregion

        #region "CONSUME FROM TARGET TABLE WITH JOIN(S)"
        private DeleteStatement TransformComplexConsume(in ConsumeStatement node, in TableReference table)
        {
            IndexInfo index = GetPrimaryOrUniqueIndex(in table) ?? throw new InvalidOperationException("CONSUME: target table has no valid index.");

            SelectExpression select = TransformConsumeToSelect(in node, in table, in index, out List<ColumnExpression> filter, out List<ColumnExpression> output);

            CommonTableExpression changes = new() { Name = "changes", Expression = select };

            DeleteStatement delete = TransformConsumeToDelete(in changes, in table, in filter, in output);

            delete.Output.Into = node.Into;

            delete.CommonTables = changes;

            //TODO: CONSUME ordering for MS SQL Server
            //TODO: 0. DECLARE @table_variable TABLE (...)
            //TODO: 1. DELETE ... OUTPUT ... INTO @table_variable
            //TODO: 2. SELECT * FROM @table_variable ORDER BY ...

            //CommonTableExpression source = new()
            //{
            //    Next = changes,
            //    Name = "source",
            //    Expression = delete
            //};

            //SelectStatement consume = TransformConsumeToSelect(in node, in source);

            //consume.CommonTables = source;

            return delete; //consume;
        }
        private SelectExpression TransformConsumeToSelect(in ConsumeStatement consume, in TableReference table, in IndexInfo index, out List<ColumnExpression> filter, out List<ColumnExpression> output)
        {
            table.Hints = "WITH (ROWLOCK" + (consume.StrictOrderRequired ? ")" : ", READPAST)");
            string targetName = (string.IsNullOrEmpty(table.Alias) ? string.Empty : table.Alias);

            SelectExpression select = new();

            filter = new List<ColumnExpression>();
            output = new List<ColumnExpression>();

            foreach (IndexColumnInfo column in index.Columns)
            {
                ColumnExpression filterColumn = new()
                {
                    Alias = column.Name,
                    Expression = new ColumnReference()
                    {
                        Binding = column,
                        Identifier = targetName + "." + column.Name,
                        Mapping = new List<ColumnMapper>()
                        {
                            new ColumnMapper()
                            {
                                Alias = column.Name,
                                Name = targetName + "." + column.Name,
                            }
                        }
                    }
                };

                filter.Add(filterColumn);
                select.Columns.Add(filterColumn);
            }

            //TODO: CreateConsumeOrder(in consume, in select, in output);

            foreach (ColumnExpression outputColumn in consume.Columns)
            {
                output.Add(outputColumn);
                select.Columns.Add(outputColumn);
            }

            select.Top = consume.Top;
            select.From = consume.From;
            select.Where = consume.Where;
            select.Order = consume.Order;

            return select;
        }
        private void CreateConsumeOrder(in ConsumeStatement consume, in SelectExpression select, in List<ColumnExpression> output)
        {
            if (consume.Order is null) { return; }

            foreach (OrderExpression consumeOrder in consume.Order.Expressions)
            {
                if (consumeOrder.Expression is ColumnReference orderColumn)
                {
                    bool found = false;

                    foreach (ColumnExpression expression in consume.Columns)
                    {
                        if (expression.Expression is ColumnReference selectColumn)
                        {
                            if (orderColumn.Identifier == selectColumn.Identifier)
                            {
                                found = true; break;
                            }
                        }
                    }

                    if (found) { continue; }

                    ParserHelper.GetColumnIdentifiers(orderColumn.Identifier, out _, out string columnName);

                    ColumnReference reference = new()
                    {
                        Binding = orderColumn.Binding,
                        Identifier = orderColumn.Identifier
                    };

                    ColumnExpression outputColumn = new()
                    {
                        Alias = columnName,
                        Expression = reference
                    };

                    if (orderColumn.Mapping is not null)
                    {
                        reference.Mapping = new List<ColumnMapper>();

                        foreach (ColumnMapper map in orderColumn.Mapping)
                        {
                            reference.Mapping.Add(new ColumnMapper()
                            {
                                Alias = columnName,
                                Name = map.Name,
                                Type = map.Type,
                                TypeName = map.TypeName
                            });
                        }
                    }

                    output.Add(outputColumn);
                    select.Columns.Add(outputColumn);
                }
            }
        }
        private DeleteStatement TransformConsumeToDelete(in CommonTableExpression changes, in TableReference table, in List<ColumnExpression> index, in List<ColumnExpression> output)
        {
            DeleteStatement delete = new()
            {
                Output = new OutputClause(),
                Target = new TableReference()
                {
                    Binding = table.Binding,
                    Identifier = "target" // !?
                },
                From = new FromClause()
                {
                    Expression = new TableJoinOperator()
                    {
                        Token = TokenType.INNER,
                        Expression1 = new TableReference()
                        {
                            Alias = "target",
                            Binding = table.Binding,
                            Identifier = table.Identifier
                        },
                        Expression2 = new TableReference()
                        {
                            Binding = changes,
                            Identifier = "changes"
                        },
                        On = new OnClause()
                        {
                            Expression = CreateDeletionFilter(in index)
                        }
                    }
                }
            };

            // OUTPUT clause - CONSUME output columns

            foreach (ColumnExpression outputColumn in output)
            {
                if (outputColumn.Expression is ColumnReference column)
                {
                    ColumnExpression expression = new() { Alias = outputColumn.Alias };

                    // 1. Ссылка                     => changes.Ссылка
                    // 2. Изменения.Ссылка           => changes.Ссылка
                    // 3. Изменения.Ссылка AS Ссылка => changes.Ссылка

                    ParserHelper.GetColumnIdentifiers(column.Identifier, out _, out string columnName);
                    if (!string.IsNullOrEmpty(outputColumn.Alias)) { columnName = outputColumn.Alias; }

                    ColumnReference reference = new()
                    {
                        Binding = column.Binding,
                        Identifier = "changes." + columnName
                    };

                    if (column.Mapping is not null)
                    {
                        reference.Mapping = new List<ColumnMapper>();

                        foreach (ColumnMapper map in column.Mapping)
                        {
                            ParserHelper.GetColumnIdentifiers(map.Name, out _, out columnName);
                            if (!string.IsNullOrEmpty(map.Alias)) { columnName = map.Alias; }

                            reference.Mapping.Add(new ColumnMapper()
                            {
                                Type = map.Type,
                                Name = "changes." + columnName
                            });
                        }
                    }

                    expression.Expression = reference;

                    delete.Output.Columns.Add(expression);
                }
                else if (outputColumn.Expression is FunctionExpression function && function.Token == TokenType.DATALENGTH)
                {
                    if (function.Parameters.Count > 0 && function.Parameters[0] is ColumnReference parameter)
                    {
                        ColumnExpression expression = new() { Alias = outputColumn.Alias };

                        ColumnReference reference = new()
                        {
                            Binding = parameter.Binding,
                            Identifier = "changes." + parameter.Identifier
                        };

                        if (parameter.Mapping is not null)
                        {
                            reference.Mapping = new List<ColumnMapper>();

                            foreach (ColumnMapper map in parameter.Mapping)
                            {
                                reference.Mapping.Add(new ColumnMapper()
                                {
                                    Name = "changes." + map.Name,
                                    Type = map.Type,
                                    Alias = map.Alias
                                });
                            }
                        }

                        expression.Expression = new FunctionExpression()
                        {
                            Name = function.Name,
                            Token = TokenType.DATALENGTH,
                            Parameters = new List<SyntaxNode>() { reference }
                        };

                        delete.Output.Columns.Add(expression);
                    }
                }
            }

            return delete;
        }
        private GroupOperator CreateDeletionFilter(in List<ColumnExpression> filter)
        {
            GroupOperator group = new();

            foreach (ColumnExpression expression in filter)
            {
                if (expression.Expression is not ColumnReference column) { continue; }

                if (group.Expression == null)
                {
                    group.Expression = CreateDeletionFilterOperator(in expression, in column);
                }
                else
                {
                    group.Expression = new BinaryOperator()
                    {
                        Token = TokenType.AND,
                        Expression1 = group.Expression,
                        Expression2 = CreateDeletionFilterOperator(in expression, in column)
                    };
                }
            }

            return group;
        }
        private ComparisonOperator CreateDeletionFilterOperator(in ColumnExpression property, in ColumnReference column)
        {
            ParserHelper.GetColumnIdentifiers(column.Identifier, out _, out string columnName);

            ColumnReference column1 = new()
            {
                Binding = column.Binding,
                Identifier = "target." + columnName
            };

            ColumnReference column2 = new()
            {
                Binding = property,
                Identifier = "changes." + columnName
            };

            if (column.Mapping is not null)
            {
                column1.Mapping = new List<ColumnMapper>();
                column2.Mapping = new List<ColumnMapper>();

                foreach (ColumnMapper map in column.Mapping)
                {
                    column1.Mapping.Add(new ColumnMapper()
                    {
                        Type = map.Type,
                        Name = "target." + columnName
                    });

                    column2.Mapping.Add(new ColumnMapper()
                    {
                        Type = map.Type,
                        Name = "changes." + columnName
                    });
                }
            }

            ComparisonOperator comparison = new()
            {
                Token = TokenType.Equals,
                Expression1 = column1,
                Expression2 = column2
            };

            return comparison;
        }
        private SelectStatement TransformConsumeToSelect(in ConsumeStatement consume, in CommonTableExpression output)
        {
            SelectStatement statement = new();

            SelectExpression select = new()
            {
                From = new FromClause()
                {
                    Expression = new TableReference()
                    {
                        Binding = output,
                        Identifier = "source"
                    }
                }
            };

            statement.Expression = select;

            foreach (ColumnExpression property in consume.Columns)
            {
                if (property.Expression is ColumnReference column)
                {
                    ColumnExpression expression = new() { Alias = property.Alias };

                    ColumnReference reference = new()
                    {
                        Binding = property,
                        Identifier = "source." + property.Alias
                    };

                    if (column.Mapping is not null)
                    {
                        reference.Mapping = new List<ColumnMapper>();

                        foreach (ColumnMapper map in column.Mapping)
                        {
                            reference.Mapping.Add(new ColumnMapper()
                            {
                                Type = map.Type,
                                Name = "source." + map.Alias
                            });
                        }
                    }

                    expression.Expression = reference;

                    select.Columns.Add(expression);
                }
                else if (property.Expression is FunctionExpression function && function.Token == TokenType.DATALENGTH)
                {
                    if (function.Parameters.Count > 0 && function.Parameters[0] is ColumnReference parameter)
                    {
                        ColumnExpression expression = new() { Alias = property.Alias };

                        ColumnReference reference = new()
                        {
                            Binding = parameter.Binding,
                            Identifier = "source." + property.Alias
                        };

                        if (parameter.Mapping is not null)
                        {
                            reference.Mapping = new List<ColumnMapper>();

                            foreach (ColumnMapper map in parameter.Mapping)
                            {
                                reference.Mapping.Add(new ColumnMapper()
                                {
                                    Type = map.Type,
                                    Name = "source." + property.Alias
                                });
                            }
                        }

                        expression.Expression = reference;

                        select.Columns.Add(expression);
                    }
                }
            }

            if (consume.Order is not null)
            {
                select.Order = new OrderClause();

                foreach (OrderExpression expression in consume.Order.Expressions)
                {
                    if (expression.Expression is ColumnReference column)
                    {
                        ParserHelper.GetColumnIdentifiers(column.Identifier, out _, out string columnName);

                        ColumnReference reference = new()
                        {
                            Binding = column.Binding,
                            Identifier = "source." + columnName
                        };

                        if (column.Mapping is not null)
                        {
                            reference.Mapping = new List<ColumnMapper>();

                            foreach (ColumnMapper map in column.Mapping)
                            {
                                reference.Mapping.Add(new ColumnMapper()
                                {
                                    Type = map.Type,
                                    Name = "source." + columnName
                                });
                            }
                        }

                        select.Order.Expressions.Add(new OrderExpression()
                        {
                            Token = expression.Token,
                            Expression = reference
                        });
                    }
                }
            }

            return statement;
        }
        #endregion

        #endregion // CONSUME STATEMENT

        public override void Visit(in CreateTypeStatement node, in StringBuilder script)
        {
            script.Append("CREATE TYPE [").Append(node.Identifier).AppendLine("] AS TABLE");
            script.AppendLine("(");

            for (int i = 0; i < node.Columns.Count; i++)
            {
                ColumnDefinition column = node.Columns[i];

                if (column.Type is not TypeIdentifier info)
                {
                    continue;
                }

                if (i > 0) { script.AppendLine(","); }

                if (info.Binding is Type type)
                {
                    if (type == typeof(bool)) // boolean
                    {
                        script.Append('_').Append(column.Name).Append("_L").Append(' ').Append("binary(1)");
                    }
                    else if (type == typeof(decimal)) // number(p,s)
                    {
                        script.Append('_').Append(column.Name).Append("_N")
                            .Append(' ').Append("numeric(").Append(info.Qualifier1).Append(',').Append(info.Qualifier2).Append(')');
                    }
                    else if (type == typeof(DateTime)) // datetime
                    {
                        script.Append('_').Append(column.Name).Append("_T").Append(' ').Append("datetime2");
                    }
                    else if (type == typeof(string)) // string(n)
                    {
                        script.Append('_').Append(column.Name).Append("_S").Append(' ').Append("nvarchar(")
                            .Append((info.Qualifier1 > 0) ? info.Qualifier1.ToString() : "max").Append(')');
                    }
                    else if (type == typeof(byte[])) // binary
                    {
                        script.Append('_').Append(column.Name).Append("_B").Append(' ').Append("varbinary(max)");
                    }
                    else if (type == typeof(Guid)) // uuid
                    {
                        script.Append('_').Append(column.Name).Append("_U").Append(' ').Append("binary(16)");
                    }
                    else if (type == typeof(Entity)) // entity - multiple reference type
                    {
                        script.Append('_').Append(column.Name).Append("_C").Append(' ').Append("binary(4)").AppendLine(",");
                        script.Append('_').Append(column.Name).Append("_R").Append(' ').Append("binary(16)");
                    }
                    else
                    {
                        throw new InvalidOperationException("Unsupported column data type");
                    }
                }
                else if (info.Binding is Entity entity) // single reference type, example: Справочник.Номенклатура
                {
                    script.Append('_').Append(column.Name).Append("_R_").Append(entity.TypeCode).Append(' ').Append("binary(16)");
                }
                else //TODO: union type
                {
                    throw new InvalidOperationException("Unknown column data type");
                    //script.Append('_').Append(column.Name).Append("_D").Append(' ').Append("binary(1)");
                }
            }

            script.AppendLine().AppendLine(")");
        }

        public override void Visit(in CreateSequenceStatement node, in StringBuilder script)
        {
            // IF NOT EXISTS(SELECT 1 FROM sys.sequences WHERE name = '{SEQUENCE_NAME}')
            // BEGIN
            // CREATE SEQUENCE {SEQUENCE_NAME} AS numeric(19,0) START WITH 1 INCREMENT BY 1 CACHE 1;
            // END;

            script.Append("IF NOT EXISTS(SELECT 1 FROM sys.sequences WHERE name = '")
                .Append(node.Identifier).AppendLine("')")
                .AppendLine("BEGIN");

            script.Append("CREATE SEQUENCE ").Append(node.Identifier).Append(" AS ");

            if (node.DataType is TypeIdentifier info)
            {
                if (info.Binding is Type type)
                {
                    if (type == typeof(decimal)) // number(p,s))
                    {
                        if (info.Qualifier1 > 0)
                        {
                            script.Append("numeric(").Append(info.Qualifier1).Append(',').Append(info.Qualifier2).Append(')');
                        }
                        else
                        {
                            script.Append("bigint");
                        }
                    }
                    else if (type == typeof(int))
                    {
                        script.Append("int");
                    }
                    else
                    {
                        script.Append("bigint");
                    }   
                }
                else
                {
                    script.Append("bigint");
                }
            }
            else
            {
                script.Append("bigint");
            }

            script.Append(" START WITH ").Append(node.StartWith)
                .Append(" INCREMENT BY ").Append(node.Increment);

            if (node.CacheSize > 0)
            {
                script.Append(" CACHE ").Append(node.CacheSize);
            }

            script.AppendLine(";").AppendLine("END;");
        }
        private static string CreateSequenceTriggerName(string tableName)
        {
            return $"{tableName.ToLowerInvariant()}_instead_of_insert";
        }
        public override void Visit(in ApplySequenceStatement node, in StringBuilder script)
        {
            if (string.IsNullOrWhiteSpace(node.Identifier))
            {
                throw new InvalidOperationException("[APPLY SEQUENCE] Sequence identifier missing");
            }

            if (node.Table.Binding is not ApplicationObject table)
            {
                throw new InvalidOperationException("[APPLY SEQUENCE] Unsupported table binding");
            }

            if (node.Column.Binding is not MetadataProperty sequence)
            {
                throw new InvalidOperationException("[APPLY SEQUENCE] Unsupported column binding");
            }

            string triggerName = CreateSequenceTriggerName(table.TableName);

            script.Append("IF OBJECT_ID('").Append(triggerName).AppendLine("', 'TR') IS NULL");

            script.Append("EXECUTE('CREATE TRIGGER ")
                .Append(triggerName).Append(" ON ").Append(table.TableName)
                .AppendLine(" INSTEAD OF INSERT NOT FOR REPLICATION AS");

            bool use_comma = false;
            StringBuilder values = new();
            StringBuilder columns = new();

            MetadataColumn column;
            MetadataProperty property;
            string sequenceColumn = string.Empty;

            for (int p = 0; p < table.Properties.Count; p++)
            {
                property = table.Properties[p];

                for (int c = 0; c < property.Columns.Count; c++)
                {
                    column = property.Columns[c];

                    if (use_comma)
                    {
                        values.Append(',').Append(' ');
                        columns.Append(',').Append(' ');
                    }
                    else { use_comma = true; }

                    columns.Append(column.Name);

                    if (property.Name == sequence.Name)
                    {
                        sequenceColumn = column.Name;

                        values.Append("NEXT VALUE FOR ").Append(node.Identifier);
                    }
                    else
                    {
                        values.Append('i').Append('.').Append(column.Name);
                    }
                }
            }

            script.Append("INSERT ").Append(table.TableName).Append('(').Append(columns).Append(')').AppendLine();
            script.Append("SELECT ").Append(values).AppendLine();
            script.AppendLine("FROM INSERTED AS i;');"); // close EXECUTE statement

            if (node.ReCalculate)
            {
                script.AppendLine();
                script.Append(CreateReCalculateSequenceColumnScript(table.TableName, in sequenceColumn, node.Identifier));
            }
        }
        public override void Visit(in RevokeSequenceStatement node, in StringBuilder script)
        {
            if (string.IsNullOrWhiteSpace(node.Identifier))
            {
                throw new InvalidOperationException("[REVOKE SEQUENCE] Sequence identifier missing");
            }

            if (node.Table.Binding is not ApplicationObject table)
            {
                throw new InvalidOperationException("[REVOKE SEQUENCE] Unsupported table binding");
            }

            string triggerName = CreateSequenceTriggerName(table.TableName);

            script.Append("IF OBJECT_ID('").Append(triggerName).Append("', 'TR') IS NOT NULL ")
                .AppendLine("DROP TRIGGER ").Append(triggerName).Append(';').AppendLine();
        }
        private string CreateReCalculateSequenceColumnScript(in string tableName, in string columnName, in string sequenceName)
        {
            StringBuilder script = new();

            IndexInfo index = GetPrimaryOrUniqueIndex(in tableName)
                ?? throw new InvalidOperationException($"[APPLY SEQUENCE RECALCULATE]: Primary or unique index missing for table [{tableName}]");

            string temporaryTable = $"#COPY{tableName}";

            StringBuilder columns = new();
            StringBuilder orderby = new();
            StringBuilder joinon = new();

            IndexColumnInfo column;

            for (int i = 0; i < index.Columns.Count; i++)
            {
                column = index.Columns[i];

                if (i > 0)
                {
                    columns.Append(',').Append(' ');
                    orderby.Append(',').Append(' ');
                    joinon.Append(" AND ");
                }

                columns.Append(column.Name);
                orderby.Append(column.Name).Append(' ').Append(column.IsDescending ? "DESC" : "ASC");
                joinon.Append('T').Append('.').Append(column.Name)
                    .Append(" = ");
                joinon.Append('S').Append('.').Append(column.Name);
            }

            script.AppendLine("BEGIN TRANSACTION;");

            script.Append($"SELECT {columns}");
            script.AppendLine($", NEXT VALUE FOR {sequenceName} OVER (ORDER BY {orderby}) AS sequence_value");
            script.AppendLine($"INTO {temporaryTable} FROM {tableName} WITH (TABLOCKX, HOLDLOCK);");

            script.AppendLine($"UPDATE T SET T.{columnName} = S.sequence_value FROM {tableName} AS T");
            script.AppendLine($"INNER JOIN {temporaryTable} AS S ON {joinon};");

            script.AppendLine($"DROP TABLE {temporaryTable};");

            script.AppendLine("COMMIT TRANSACTION;");

            return script.ToString();
        }
    }
}

// Шаблон запроса на деструктивное чтение с обогащением данных (JOIN)
//DECLARE @result TABLE(id binary(16));
//WITH changes AS 
//(SELECT TOP (10)
//Изменения._NodeTRef AS УзелОбмена_TRef, Изменения._NodeRRef AS УзелОбмена_RRef,
//Изменения._IDRRef AS Ссылка
//FROM _ReferenceChngR1253 AS Изменения WITH (ROWLOCK, READPAST)
//ORDER BY _IDRRef DESC
//)
//DELETE target
//OUTPUT
//changes.Ссылка
//INTO @result
//FROM _ReferenceChngR1253 AS target INNER JOIN changes ON target._IDRRef = changes.Ссылка
//;
//SELECT * FROM @result ORDER BY id ASC;
//;

// Шаблон запроса на деструктивное чтение для Microsoft SQL Server
//WITH queue AS
//(SELECT TOP (@MessageCount)
//  МоментВремени, Идентификатор, ДатаВремя,
//  Отправитель, Получатели, Заголовки,
//  ТипОперации, ТипСообщения, ТелоСообщения
//FROM
//  {TABLE_NAME} WITH (ROWLOCK, READPAST)
//ORDER BY
//  МоментВремени ASC,
//  Идентификатор ASC
//)
//DELETE queue OUTPUT
//  deleted.МоментВремени, deleted.Идентификатор, deleted.ДатаВремя,
//  deleted.Отправитель, deleted.Получатели, deleted.Заголовки,
//  deleted.ТипОперации, deleted.ТипСообщения, deleted.ТелоСообщения
//;
// ??? OPTION (MAXDOP 1) ???