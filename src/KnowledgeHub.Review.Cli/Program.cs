using KnowledgeHub.Review;
using KnowledgeHub.Review.Config;

var options = ReviewOptions.FromEnvironment();

return await CliCommands.BuildRootCommand(options).Parse(args).InvokeAsync();
