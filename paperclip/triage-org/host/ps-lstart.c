/*
 * A stand-in for `ps -o lstart= -p <pid>` that works inside the service sandbox.
 *
 * Paperclip decides whether an agent's old run has really stopped by signalling
 * its recorded pid and, when that pid answers, comparing the live process's start
 * time with the one it recorded (`ps -o lstart= -p <pid>`, services/hot-restart.js).
 * Inside paperclip-service.sb neither half works: `(deny signal)` turns kill(pid, 0)
 * on any process outside the sandbox into EPERM, which Paperclip reads as "alive",
 * and macOS refuses to exec the setuid /bin/ps from a sandboxed process at all. So
 * once the kernel reuses an old run's pid for an unrelated process, Paperclip can
 * never prove that run stopped, and pausing or terminating that agent never
 * completes. Measured on the mini, 2026-11-25: a lead stuck in "pausing" for five
 * hours on a pid that had become /usr/libexec/knowledge-agent.
 *
 * The start time comes from sysctl(KERN_PROC_PID), which needs no privilege, so
 * this binary is not setuid and runs inside the sandbox. It answers exactly that
 * one invocation; anything else is handed to /bin/ps untouched, which behaves as
 * it did before (it still cannot run inside the sandbox). It is installed ahead of
 * /bin on the service's PATH and nowhere else.
 */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include <unistd.h>
#include <sys/sysctl.h>

static int lstart(const char *arg)
{
    char *end;
    long pid = strtol(arg, &end, 10);
    if (*arg == '\0' || *end != '\0' || pid <= 0)
        return 1;
    int mib[4] = { CTL_KERN, KERN_PROC, KERN_PROC_PID, (int)pid };
    struct kinfo_proc kp;
    size_t len = sizeof kp;
    memset(&kp, 0, sizeof kp);
    if (sysctl(mib, 4, &kp, &len, NULL, 0) != 0 || len == 0)
        return 1; /* no such process: ps prints nothing and exits 1 */
    time_t started = kp.kp_proc.p_starttime.tv_sec;
    char out[64];
    /* ps's own lstart format, e.g. "Tue Nov 24 19:53:49 2026" */
    if (strftime(out, sizeof out, "%a %b %e %H:%M:%S %Y", localtime(&started)) == 0)
        return 1;
    printf("%s\n", out);
    return 0;
}

int main(int argc, char **argv)
{
    if (argc == 5 && strcmp(argv[1], "-o") == 0 && strcmp(argv[2], "lstart=") == 0
        && strcmp(argv[3], "-p") == 0)
        return lstart(argv[4]);
    argv[0] = "/bin/ps";
    execv("/bin/ps", argv);
    perror("/bin/ps");
    return 127;
}
