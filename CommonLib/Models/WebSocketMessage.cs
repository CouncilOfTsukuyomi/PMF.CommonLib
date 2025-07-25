﻿using Newtonsoft.Json;

namespace CommonLib.Models;

public static class WebSocketMessageType
{
    public const string Progress = "progress_update";
    public const string Error = "error";
    public const string Log = "log";
    public const string Status = "status";
    public const string ConfigurationChange = "configuration_change";
}

public static class WebSocketMessageStatus
{
    public const string InProgress = "in_progress";
    public const string Error = "error";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Queued = "queued";
}

public class WebSocketMessage
{
    [JsonProperty("type")]
    public string Type { get; set; }
    
    [JsonProperty("task_id")]
    public string TaskId { get; set; }
    
    [JsonProperty("status")]
    public string Status { get; set; }
    
    [JsonProperty("progress")]
    public int Progress { get; set; }
    
    [JsonProperty("message")]
    public string Message { get; set; }
    
    [JsonProperty("client_id")]
    public string ClientId { get; set; }

    [JsonProperty("title")]
    public string Title { get; set; } = "General";

    public static WebSocketMessage CreateProgress(string taskId, int progress, string message, string title = null)
    {
        return new WebSocketMessage
        {
            Type = WebSocketMessageType.Progress,
            TaskId = taskId,
            Status = WebSocketMessageStatus.InProgress,
            Progress = progress,
            Message = message,
            Title = title ?? "General"
        };
    }

    public static WebSocketMessage CreateError(string taskId, string errorMessage, string title = null)
    {
        return new WebSocketMessage
        {
            Type = WebSocketMessageType.Error,
            TaskId = taskId,
            Status = WebSocketMessageStatus.Failed,
            Progress = 0,
            Message = errorMessage,
            Title = title ?? "General"
        };
    }

    public static WebSocketMessage CreateStatus(string taskId, string status, string message, string title = null)
    {
        return new WebSocketMessage
        {
            Type = WebSocketMessageType.Status,
            TaskId = taskId,
            Status = status,
            Progress = 0,
            Message = message,
            Title = title ?? "General"
        };
    }
}