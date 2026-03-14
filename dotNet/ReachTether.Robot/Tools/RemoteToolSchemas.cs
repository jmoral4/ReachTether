internal static class RemoteToolSchemas
{
    public static readonly object Scheduler = new
    {
        type = "object",
        additionalProperties = false,
        properties = new
        {
            title = new { type = "string", description = "Short reminder or meeting title." },
            when = new { type = "string", description = "Requested time or schedule in natural language." },
            details = new { type = new[] { "string", "null" }, description = "Optional reminder details." }
        },
        required = new[] { "title", "when", "details" }
    };

    public static readonly object KinectShot = new
    {
        type = "object",
        additionalProperties = false,
        properties = new
        {
            question = new { type = "string", description = "What to look for in the remote camera image." }
        },
        required = new[] { "question" }
    };

    public static readonly object SmartyMode = new
    {
        type = "object",
        additionalProperties = false,
        properties = new
        {
            prompt = new { type = "string", description = "The question or task to offload to a smarter model." }
        },
        required = new[] { "prompt" }
    };

    public static readonly object MemoryQuery = new
    {
        type = "object",
        additionalProperties = false,
        properties = new
        {
            query = new { type = "string", description = "Natural-language question to search session memory and knowledge." }
        },
        required = new[] { "query" }
    };
}
