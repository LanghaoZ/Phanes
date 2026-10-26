namespace Phanes.Task.Sdk;

using System.Threading.Tasks;

/// <summary>
/// Declares the task_type an ITask implementation handles. The framework
/// discovers tasks by scanning assemblies for this attribute.
///
/// Naming convention: "&lt;owner&gt;.&lt;name&gt;" — first-party tasks use "phanes.",
/// plugin tasks use their service's prefix, e.g. "openinsights.data_ingestion".
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class PhanesTaskAttribute(string taskType) : Attribute
{
    public string TaskType { get; } = taskType;
}
