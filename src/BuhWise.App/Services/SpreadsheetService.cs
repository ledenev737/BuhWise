using System;
using System.Collections.Generic;
using System.Globalization;
using BuhWise.Models;
using ClosedXML.Excel;

namespace BuhWise.Services
{
    public class SpreadsheetService
    {
        private static readonly string[] RequiredColumns =
        {
            "Date",
            "OperationType",
            "FromCurrency",
            "FromAmount",
            "ToCurrency",
            "ToAmount",
            "Rate"
        };

        private static readonly string[] ExportHeaders =
        {
            "Id",
            "Date",
            "OperationType",
            "FromCurrency",
            "FromAmount",
            "ToCurrency",
            "ToAmount",
            "Rate",
            "Fee",
            "UsdEquivalent",
            "ExpenseCategory",
            "Comment"
        };

        public void ExportOperations(string filePath, IEnumerable<Operation> operations)
        {
            using var workbook = new XLWorkbook();
            var worksheet = workbook.AddWorksheet("Transactions");

            for (var i = 0; i < ExportHeaders.Length; i++)
            {
                worksheet.Cell(1, i + 1).Value = ExportHeaders[i];
            }

            var row = 2;
            foreach (var operation in operations)
            {
                worksheet.Cell(row, 1).Value = operation.Id;
                worksheet.Cell(row, 2).Value = operation.Date;
                worksheet.Cell(row, 3).Value = operation.Type.ToString();
                worksheet.Cell(row, 4).Value = operation.SourceCurrency;
                worksheet.Cell(row, 5).Value = operation.SourceAmount;
                worksheet.Cell(row, 6).Value = operation.TargetCurrency;
                worksheet.Cell(row, 7).Value = operation.TargetAmount;
                worksheet.Cell(row, 8).Value = operation.Rate;
                worksheet.Cell(row, 9).Value = operation.Commission;
                worksheet.Cell(row, 10).Value = operation.UsdEquivalent;
                worksheet.Cell(row, 11).Value = operation.ExpenseCategory;
                worksheet.Cell(row, 12).Value = operation.ExpenseComment;
                row++;
            }

            worksheet.Columns().AdjustToContents();
            workbook.SaveAs(filePath);
        }

        public List<Operation> ImportOperations(string filePath)
        {
            using var workbook = new XLWorkbook(filePath);
            var worksheet = workbook.Worksheets.Worksheet(1);

            var headerRow = worksheet.FirstRowUsed();
            if (headerRow == null)
            {
                throw new InvalidOperationException("Пустой файл XLSX");
            }

            var lastColumn = headerRow.LastCellUsed()?.Address.ColumnNumber ?? 0;
            var headerMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var col = 1; col <= lastColumn; col++)
            {
                var header = headerRow.Cell(col).GetString().Trim();
                if (!string.IsNullOrWhiteSpace(header))
                {
                    headerMap[header] = col;
                }
            }

            foreach (var required in RequiredColumns)
            {
                if (!headerMap.ContainsKey(required))
                {
                    throw new InvalidOperationException($"Отсутствует обязательный столбец \"{required}\"");
                }
            }

            var operations = new List<Operation>();
            var lastRow = worksheet.LastRowUsed()?.RowNumber() ?? 1;
            for (var row = 2; row <= lastRow; row++)
            {
                var dateCell = worksheet.Cell(row, headerMap["Date"]);
                if (dateCell.IsEmpty())
                {
                    continue;
                }

                var id = 0L;
                if (headerMap.TryGetValue("Id", out var idCol))
                {
                    var idCell = worksheet.Cell(row, idCol);
                    if (!idCell.IsEmpty())
                    {
                        id = (long)Math.Round(ReadDouble(idCell, "Id", row));
                    }
                }

                var operation = new Operation
                {
                    Id = id,
                    Date = ReadDate(dateCell, row),
                    Type = ParseOperationType(worksheet.Cell(row, headerMap["OperationType"]).GetString()),
                    SourceCurrency = ParseCurrency(worksheet.Cell(row, headerMap["FromCurrency"]).GetString()),
                    SourceAmount = ReadDouble(worksheet.Cell(row, headerMap["FromAmount"]), "FromAmount", row),
                    TargetCurrency = ParseCurrency(worksheet.Cell(row, headerMap["ToCurrency"]).GetString()),
                    TargetAmount = ReadDouble(worksheet.Cell(row, headerMap["ToAmount"]), "ToAmount", row),
                    Rate = ReadDouble(worksheet.Cell(row, headerMap["Rate"]), "Rate", row)
                };

                if (headerMap.TryGetValue("Fee", out var feeCol))
                {
                    var feeCell = worksheet.Cell(row, feeCol);
                    operation.Commission = feeCell.IsEmpty() ? null : ReadDouble(feeCell, "Fee", row);
                }

                if (headerMap.TryGetValue("ExpenseCategory", out var categoryCol))
                {
                    var value = worksheet.Cell(row, categoryCol).GetString();
                    operation.ExpenseCategory = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
                }

                if (headerMap.TryGetValue("Comment", out var commentCol))
                {
                    var value = worksheet.Cell(row, commentCol).GetString();
                    operation.ExpenseComment = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
                }

                operation.UsdEquivalent = ReadUsdEquivalent(worksheet, headerMap, row, operation);

                operations.Add(operation);
            }

            if (operations.Count == 0)
            {
                throw new InvalidOperationException("Файл не содержит операций");
            }

            return operations;
        }

        private static double ReadUsdEquivalent(IXLWorksheet worksheet, Dictionary<string, int> headerMap, int row, Operation operation)
        {
            if (headerMap.TryGetValue("UsdEquivalent", out var column))
            {
                var cell = worksheet.Cell(row, column);
                if (!cell.IsEmpty())
                {
                    return ReadDouble(cell, "UsdEquivalent", row);
                }
            }

            var calculated = TryCalculateUsdEquivalent(operation);
            if (calculated.HasValue)
            {
                return calculated.Value;
            }

            throw new InvalidOperationException($"Для строки {row} не удалось определить USD-эквивалент. Добавьте столбец UsdEquivalent.");
        }

        private static double? TryCalculateUsdEquivalent(Operation operation)
        {
            return operation.Type switch
            {
                OperationType.Income => string.Equals(operation.SourceCurrency, "USD", StringComparison.OrdinalIgnoreCase)
                    ? operation.SourceAmount
                    : operation.SourceAmount * operation.Rate,
                OperationType.Expense => string.Equals(operation.SourceCurrency, "USD", StringComparison.OrdinalIgnoreCase)
                    ? operation.SourceAmount
                    : operation.SourceAmount * operation.Rate,
                OperationType.Exchange when string.Equals(operation.TargetCurrency, "USD", StringComparison.OrdinalIgnoreCase) => operation.TargetAmount,
                OperationType.Exchange when string.Equals(operation.SourceCurrency, "USD", StringComparison.OrdinalIgnoreCase) => operation.SourceAmount,
                _ => null
            };
        }

        private static OperationType ParseOperationType(string value)
        {
            var normalized = value.Trim();
            return normalized switch
            {
                "Income" or "Пополнение" => OperationType.Income,
                "Expense" or "Расход" => OperationType.Expense,
                "Exchange" or "Обмен" => OperationType.Exchange,
                _ => throw new InvalidOperationException($"Неизвестный тип операции: {value}")
            };
        }

        private static string ParseCurrency(string value)
        {
            var normalized = value.Trim().ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                throw new InvalidOperationException("Пустое значение валюты");
            }

            return normalized;
        }

        private static DateTime ReadDate(IXLCell cell, int row)
        {
            if (cell.TryGetValue(out DateTime date))
            {
                return date;
            }

            var text = cell.GetString();
            if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed))
            {
                return parsed;
            }

            throw new InvalidOperationException($"Некорректная дата в строке {row}");
        }

        private static double ReadDouble(IXLCell cell, string column, int row)
        {
            if (cell.TryGetValue(out double value))
            {
                return value;
            }

            var text = cell.GetString();
            if (double.TryParse(text.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }

            throw new InvalidOperationException($"Некорректное значение {column} в строке {row}");
        }
    }
}
