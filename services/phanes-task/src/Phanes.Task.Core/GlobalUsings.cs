// 'Task' resolves to the Phanes.Task namespace rather than System.Threading.Tasks.Task
// because the compiler finds 'Task' as a child namespace of 'Phanes' before reaching
// the System.Threading.Tasks global using. This global alias restores the expected binding.
global using Task = System.Threading.Tasks.Task;

// Proto-generated wire types are referenced throughout the framework; pull them in
// project-wide so individual files don't each repeat the using.
global using Phanes.Task.Contracts;
