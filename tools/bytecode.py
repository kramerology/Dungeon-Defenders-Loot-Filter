"""Read-only UE3 bytecode listing (adapted from DungeonRoutes tools/ai_bytecode.py). Usage: bytecode.py PID Owner.Function ..."""
import struct as S, sys
from probe import M, RT
m=M(int(sys.argv[1])); r=RT(m)
objects=r.objs
def obj(p):
    o=objects.get(p,{})
    return o.get('name',hex(p))
natives={m.u32(p+0x94)&65535:o['name'] for p,o in objects.items() if o['type']=='Function' and m.u32(p+0x94)&65535}
class Listing:
    def __init__(self,data): self.data=data; self.pos=0
    def read(self,fmt):
        value=S.unpack_from('<'+fmt,self.data,self.pos); self.pos+=S.calcsize('<'+fmt); return value[0] if len(value)==1 else value
    def ref(self): return obj(self.read('Q'))
    def fname(self):
        idx,num=self.read('II'); return r.names.get(idx,'NAME_'+str(idx)) + ('_'+str(num-1) if num else '')
    def args(self):
        values=[]
        while self.data[self.pos]!=0x16: values.append(self.expr())
        self.pos+=1; return ', '.join(values)
    def expr(self):
        at=self.pos; op=self.read('B')
        if op>=0x60:
            native=(op-0x60)*256+self.read('B') if op<0x70 else op
            return natives.get(native,'native_'+hex(native))+'('+self.args()+')'
        if op in [0,1,2,3,0x29,0x48]: return self.ref()
        if op in [0x0b,0x4a,0x15]: return '<empty>'
        if op==0x53: return '<end>'
        if op in [0x0f,0x14]: return self.expr()+' = '+self.expr()
        if op==4: return 'return '+self.expr()
        if op==6: return 'goto '+hex(self.read('H'))
        if op==7:
            dest=self.read('H'); return 'if_not ('+self.expr()+') goto '+hex(dest)
        if op==0x18: self.read('H'); return self.expr()
        if op in [0x19,0x12]:
            context=self.expr(); self.read('H'); self.read('Q'); self.read('B'); return context+'.'+self.expr()
        if op==0x1b or op==0x37: return self.fname()+'('+self.args()+')'
        if op==0x1c: return self.ref()+'('+self.args()+')'
        if op==0x05:
            self.ref(); self.read('B'); return 'switch ('+self.expr()+')'
        if op==0x0a:
            nxt=self.read('H')
            return 'default:' if nxt==0xffff else 'case '+self.expr()+': (next '+hex(nxt)+')'
        if op==0x1d: return str(self.read('i'))
        if op==0x1e: return str(self.read('f'))
        if op==0x20: return 'object '+self.ref()
        if op==0x21: return "'"+self.fname()+"'"
        if op==0x23: return 'Vector'+str(self.read('fff'))
        if op in [0x24,0x2c]: return str(self.read('B'))
        if op in [0x25,0x26]: return str(op-0x25)
        if op==0x27: return 'true'
        if op==0x28: return 'false'
        if op==0x2a: return 'None'
        if op==0x2d: return self.expr()
        if op in [0x13,0x2e,0x52]: return self.ref()+'('+self.expr()+')'
        if op==0x38: return 'cast_'+hex(self.read('B'))+'('+self.expr()+')'
        if op==0x17: return 'self'
        if op==0x3a: return '<return value '+self.ref()+'>'
        if op==0x36: return self.expr()+'.Length'
        if op==0x35:
            member=self.ref(); self.ref(); self.read('BB'); return self.expr()+'.'+member
        if op==0x58:
            array=self.expr(); item=self.expr(); hasindex=self.read('B'); index=self.expr(); end=self.read('H')
            return 'foreach '+array+'('+item+(', '+index if hasindex else '')+') until '+hex(end)
        if op==0x31: return 'iterator_next'
        if op==0x30: return 'iterator_end'
        if op in [0x10,0x1a]:
            index=self.expr(); return self.expr()+'['+index+']'
        if op==0x46:
            array=self.expr(); self.read('H'); value=self.expr(); assert self.read('B')==0x16
            return array+'.Find('+value+')'
        if op==0x45:
            condition=self.expr(); self.read('H'); yes=self.expr(); self.read('H'); no=self.expr()
            return '('+condition+' ? '+yes+' : '+no+')'
        if op==0x49:
            self.read('H'); value=self.expr(); assert self.read('B')==0x15; return '<default '+value+'>'
        if op==0x0d: return 'goto_label '+self.expr()
        if op==0x51: return self.expr()
        if op==0x09:
            line=self.read('H'); self.read('B'); return 'assert '+self.expr()
        raise ValueError('Unsupported token '+hex(op)+' at '+hex(at))
for want in sys.argv[2:]:
    owner,name=want.split('.')
    for p,o in objects.items():
        if o['type']!='Function' or o['name']!=name or objects.get(o['outer'],{}).get('name')!=owner: continue
        ptr,n,_=S.unpack('<III',m.read(p+0x54,12))
        if not n: print('\nNATIVE/EMPTY',want); continue
        listing=Listing(m.read(ptr,n)); print('\nFUNCTION',want)
        try:
            while listing.pos<n:
                pos=listing.pos; print(hex(pos),listing.expr())
        except Exception as e: print('STOP:',e)
