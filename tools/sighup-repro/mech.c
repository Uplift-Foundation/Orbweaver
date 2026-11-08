// mech <log> <stopped:0|1> : leader L in its own pgrp; Z = stopped member (if 1);
// A forks B (sleeps 2s) then A exits -> B orphaned to launchd; B exits -> jobc--.
#include <signal.h>
#include <stdio.h>
#include <stdlib.h>
#include <unistd.h>
#include <string.h>
#include <fcntl.h>
#include <sys/wait.h>
static int fd; static volatile int got=0;
static void h(int s, siginfo_t *si, void *u){ char b[160]; int n=snprintf(b,sizeof b,"L got sig=%d si_pid=%d si_code=%d\n",s,si->si_pid,si->si_code); write(fd,b,n); if(s==SIGHUP) got=1;}
int main(int c,char**v){ fd=open(v[1],O_WRONLY|O_CREAT|O_TRUNC,0644); int stopped=atoi(v[2]);
 if(getenv("DO_SETSID")) setsid();
 struct sigaction sa; memset(&sa,0,sizeof sa); sa.sa_sigaction=h; sa.sa_flags=SA_SIGINFO; sigaction(SIGHUP,&sa,0); sigaction(SIGCONT,&sa,0);
 char b[160]; int n=snprintf(b,sizeof b,"L pid=%d pgid=%d sid=%d ppid=%d stopped=%d\n",getpid(),getpgrp(),getsid(0),getppid(),stopped); write(fd,b,n);
 pid_t z=0; if(stopped){ z=fork(); if(z==0){ signal(SIGHUP,SIG_DFL); kill(getpid(),SIGSTOP); pause(); _exit(0);} usleep(200000);} 
 pid_t a=fork(); if(a==0){ pid_t bb=fork(); if(bb==0){ sleep(2); _exit(0);} _exit(0);} waitpid(a,0,0);
 sleep(4);
 n=snprintf(b,sizeof b,"L done gotHUP=%d\n",got); write(fd,b,n);
 if(z){ kill(z,SIGKILL); waitpid(z,0,0);} return 0;}
