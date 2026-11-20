import subprocess, json, selectors
path='/home/andrii/RiderProjects/tgcli/TgCli.Tests/McpTestServer/bin/Debug/net10.0/TgCli.McpTestServer.dll'
meta={'io.modelcontextprotocol/protocolVersion':'2026-07-28','io.modelcontextprotocol/clientCapabilities':{}}
print('assembly='+path)
for label, seq in [
 ('malformed_initialize', [('initialize',{}),('tools/list',{}),('tools/list',{'_meta':meta})]),
 ('switch_after_modern', [('tools/list',{'_meta':meta}),('initialize',{'protocolVersion':'2025-11-25','capabilities':{},'clientInfo':{'name':'probe','version':'1'}}),('tools/list',{})])
]:
 p=subprocess.Popen(['dotnet',path],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True)
 sel=selectors.DefaultSelector();sel.register(p.stdout,selectors.EVENT_READ)
 print(label)
 try:
  for i,(method,params) in enumerate(seq,1):
   request={'jsonrpc':'2.0','id':i,'method':method,'params':params}
   print('request='+json.dumps(request))
   p.stdin.write(json.dumps(request)+'\n');p.stdin.flush()
   if not sel.select(5): print('NO RESPONSE');break
   v=json.loads(p.stdout.readline());print('response='+json.dumps(v) if 'error' in v else 'response_result_keys='+str(list(v.get('result',{}).keys())))
  p.stdin.close();p.wait(timeout=5)
 except Exception as e: print(type(e).__name__);p.kill();p.wait()
 finally: p.stdout.close();print('exit='+str(p.returncode));print('stderr='+p.stderr.read()[:200]);p.stderr.close();sel.close()
