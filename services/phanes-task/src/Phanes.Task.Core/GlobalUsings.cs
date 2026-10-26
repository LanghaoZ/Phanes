// NOTE on the `Task` name: inside any `Phanes.Task.*` namespace, the bare
// identifier `Task` binds to the Phanes.Task NAMESPACE (found while walking
// outward) before compilation-unit usings are consulted — so a global alias
// cannot fix it. Files that use System.Threading.Tasks.Task place
// `using System.Threading.Tasks;` AFTER their file-scoped namespace line;
// a using directive inside the namespace declaration is consulted first.

// Proto-generated wire types are referenced throughout the framework.
global using Phanes.Task.Contracts;
