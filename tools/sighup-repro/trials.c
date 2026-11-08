// trials <log> <N> <grp|sess> cmd...: N trials; each child gets a fresh pgrp (grp) or session (sess), execs cmd.
#include <signal.h>
#include <stdio.h>
#include <stdlib.h>
#include <unistd.h>
#include <string.h>
#include <fcntl.h>
#include <sys/wait.h>
int main(int c,char**v){ int fd=open(v[1],O_WRONLY|O_CREAT|O_APPEND,0644); int n=atoi(v[2]); int sess=strcmp(v[3],"sess")==0;
 char b[200]; int k=snprintf(b,sizeof b,"trials pid=%d pgid=%d sid=%d mode=%s\n",getpid(),getpgrp(),getsid(0),v[3]); write(fd,b,k);
 int hup=0, other=0, ok=0;
 for(int i=0;i<n;i++){ pid_t p=fork(); if(p==0){ if(sess) setsid(); else setpgid(0,0); execvp(v[4],v+4); _exit(127);} int st; waitpid(p,&st,0);
  if(WIFSIGNALED(st)&&WTERMSIG(st)==SIGHUP) hup++; else if(WIFEXITED(st)&&WEXITSTATUS(st)==0) ok++; else other++;
  k=snprintf(b,sizeof b,"trial %d: %s %d\n",i,WIFSIGNALED(st)?"signal":"exit",WIFSIGNALED(st)?WTERMSIG(st):WEXITSTATUS(st)); write(fd,b,k);}
 k=snprintf(b,sizeof b,"SUMMARY mode=%s trials=%d sighup=%d ok=%d other=%d\n",v[3],n,hup,ok,other); write(fd,b,k); return 0;}
