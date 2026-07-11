using System;
using System.IO;
using System.Text.Json;

namespace DivaModManager
{
    internal static class UserErrorMessage
    {
        internal static string From(Exception exception)
        {
            if (exception == null)
                return "发生未知错误。";

            var message = exception.Message?.Trim();
            if (IsChineseUserMessage(message))
                return message;

            return exception switch
            {
                UnauthorizedAccessException => "没有权限访问所需的文件或目录。请检查权限后重试。",
                FileNotFoundException fileNotFound when !String.IsNullOrWhiteSpace(fileNotFound.FileName) =>
                    $"找不到所需文件：{fileNotFound.FileName}",
                FileNotFoundException => "找不到所需文件。",
                DirectoryNotFoundException => "找不到所需目录。",
                PathTooLongException => "文件或目录路径过长。",
                JsonException => "保存的配置文件格式无效或已损坏。",
                InvalidDataException => "文件数据格式无效或文件已损坏。",
                IOException => "文件读写失败。请确认文件未被其他程序占用，并检查路径与权限。",
                ArgumentException => "参数或文件路径无效。",
                NotSupportedException => "当前文件或数据格式不受支持。",
                _ => "操作发生未知错误。详细信息已写入日志。"
            };
        }

        private static bool IsChineseUserMessage(string value)
        {
            if (String.IsNullOrWhiteSpace(value))
                return false;

            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];
                if (character >= '\u3400' && character <= '\u9fff')
                    return index <= 16;
            }

            return false;
        }
    }
}
