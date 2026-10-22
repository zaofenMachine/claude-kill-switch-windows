import http from 'node:http';
export function api(method, path, value) {
 return new Promise((resolve,reject) => {
  const body=value === undefined ? null : JSON.stringify(value);
  const req=http.request({socketPath:String.raw`\\.\pipe\verge-mihomo`,path,method,
    headers:body ? {'Content-Type':'application/json','Content-Length':Buffer.byteLength(body)} : {}},res=>{
   let text='';res.setEncoding('utf8');res.on('data',s=>{text+=s;if(text.length>16*1024*1024)req.destroy(new Error('Oversized API response'));});
   res.on('end',()=>{if(res.statusCode<200||res.statusCode>=300)return reject(new Error('Mihomo API HTTP '+res.statusCode));
    try{resolve(text ? JSON.parse(text):null);}catch{reject(new Error('Invalid API JSON'));}});
 });const timer=setTimeout(()=>req.destroy(new Error('Mihomo API deadline exceeded')),10000);
 req.on('close',()=>clearTimeout(timer));req.setTimeout(10000,()=>req.destroy(new Error('Mihomo API timeout')));req.on('error',reject);req.end(body);
 });
}
