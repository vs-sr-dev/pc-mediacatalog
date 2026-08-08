using Xunit;

// The plugin registry is static, because whether ".epub" is a media file is a property of
// the installation rather than of whoever is asking. That makes it shared state, and a test
// that loads a plugin while another is asserting what the vocabulary holds is a test that
// fails for reasons nobody can reproduce. Running the suite in one thread costs a second or
// two and removes the whole class of problem.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
