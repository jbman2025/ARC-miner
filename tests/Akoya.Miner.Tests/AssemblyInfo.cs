// Metrics is process-global static state and the slot tests reset it between
// cases, so nothing in this assembly may run concurrently with them. The suite
// is pure-function only and takes well under a second, so serialising the whole
// assembly costs nothing and removes a whole class of flakiness.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
