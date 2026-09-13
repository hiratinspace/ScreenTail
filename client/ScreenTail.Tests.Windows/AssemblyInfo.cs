using Xunit.Sdk;
using Xunit.v3;

// These tests observe machine-wide state: which window is in front, whether a hook is installed, what the
// keyboard is doing. Running two of them at once means one test's windows and keystrokes land inside
// another's measurement — which is exactly what happened: a "while nothing is happening" idle-CPU reading
// came back at 0.519% because the input tests were creating windows and taking focus alongside it.
//
// The desktop is a single shared resource and these tests take turns with it.
[assembly: Parallelization(Mode = ParallelMode.None)]
