﻿using Microsoft.Data.Sqlite;
using System.Data.Common;

namespace DaJet.Data.SqlServer
{
    internal sealed class SqliteConnectionFactory : IDbConnectionFactory
    {
        public DbConnection Create(in Uri uri)
        {
            return new SqliteConnection(GetConnectionString(in uri));
        }
        public DbConnection Create(in string connectionString)
        {
            return new SqliteConnection(connectionString);
        }
        private static string GetDatabaseFilePath(in Uri uri)
        {
            if (uri.Scheme != "sqlite")
            {
                throw new InvalidOperationException(uri.ToString());
            }

            string filePath = uri.AbsoluteUri.Replace("sqlite://", string.Empty);

            int question = filePath.IndexOf('?');

            if (question > -1)
            {
                filePath = filePath[..question];
            }

            filePath = filePath.TrimEnd('/').TrimEnd('\\').Replace('/', '\\');

            string databasePath = Path.Combine(AppContext.BaseDirectory, filePath);

            if (!File.Exists(databasePath))
            {
                throw new FileNotFoundException(databasePath);
            }

            if (Environment.OSVersion.Platform == PlatformID.Unix)
            {
                databasePath = databasePath.Replace('\\', '/');
            }

            return databasePath;
        }
        public string GetConnectionString(in Uri uri)
        {
            var builder = new SqliteConnectionStringBuilder()
            {
                Mode = SqliteOpenMode.ReadWriteCreate,
                DataSource = GetDatabaseFilePath(in uri)
            };

            return builder.ToString();
        }
        public int GetYearOffset(in Uri uri)
        {
            using (SqliteConnection connection = new(GetConnectionString(in uri)))
            {
                connection.Open();

                using (SqliteCommand command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = '_YearOffset';";

                    object value = command.ExecuteScalar();

                    if (value is null)
                    {
                        return -1;
                    }

                    command.CommandText = "SELECT TOP 1 [Offset] FROM [_YearOffset];";

                    value = command.ExecuteScalar();

                    if (value is not int offset)
                    {
                        return 0;
                    }

                    return offset;
                }
            }
        }
        public void ConfigureParameters(in DbCommand command, in Dictionary<string, object> parameters, int yearOffset)
        {
            if (command is not SqliteCommand cmd)
            {
                throw new InvalidOperationException($"{nameof(command)} is not type of {typeof(SqliteCommand)}");
            }

            cmd.Parameters.Clear();

            foreach (var parameter in parameters)
            {
                string name = parameter.Key.StartsWith('@') ? parameter.Key[1..] : parameter.Key;

                if (parameter.Value is null)
                {
                    cmd.Parameters.AddWithValue(name, DBNull.Value);
                }
                else if (parameter.Value is Entity entity)
                {
                    cmd.Parameters.AddWithValue(name, entity.Identity.ToByteArray());
                }
                else if (parameter.Value is bool boolean)
                {
                    cmd.Parameters.AddWithValue(name, new byte[] { Convert.ToByte(boolean) });
                }
                else if (parameter.Value is DateTime dateTime)
                {
                    cmd.Parameters.AddWithValue(name, dateTime.AddYears(yearOffset));
                }
                else if (parameter.Value is Guid uuid)
                {
                    cmd.Parameters.AddWithValue(name, uuid.ToByteArray());
                }
                else // int, decimal, string, byte[]
                {
                    cmd.Parameters.AddWithValue(name, parameter.Value);
                }

                //TODO: user-defined type - table-valued parameter
                //else if (parameter.Value is List<DataObject> table)
                //{
                //    DeclareStatement declare = GetDeclareStatementByName(in model, parameter.Key);

                //    parameters[parameter.Key] = new TableValuedParameter()
                //    {
                //        Name = parameter.Key,
                //        Value = table,
                //        DbName = declare is null ? string.Empty : declare.Type.Identifier
                //    };
                //}

                //else if (parameter.Value is List<Dictionary<string, object>> table)
                //{
                //    DeclareStatement declare = GetDeclareStatementByName(in model, parameter.Key);

                //    parameters[parameter.Key] = new TableValuedParameter()
                //    {
                //        Name = parameter.Key,
                //        Value = table,
                //        DbName = declare is null ? string.Empty : declare.Type.Identifier
                //    };
                //}
            }
        }
    }
}