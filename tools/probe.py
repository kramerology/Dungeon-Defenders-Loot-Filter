"""Read-only probe: dump reflected classes/properties matching filters."""
import ctypes as C, struct as S, sys, json
from ctypes import wintypes as W
k = C.WinDLL('kernel32', use_last_error=True)
k.OpenProcess.argtypes=[W.DWORD,W.BOOL,W.DWORD]; k.OpenProcess.restype=W.HANDLE
k.ReadProcessMemory.argtypes=[W.HANDLE,C.c_void_p,C.c_void_p,C.c_size_t,C.POINTER(C.c_size_t)]
class M:
    def __init__(s,pid): s.h=k.OpenProcess(0x410,False,pid)
    def read(s,a,n):
        b=C.create_string_buffer(n); r=C.c_size_t()
        if not k.ReadProcessMemory(s.h,a,b,n,C.byref(r)): return b''
        return b.raw[:r.value]
    def u32(s,a):
        b=s.read(a,4); return S.unpack('<I',b)[0] if len(b)==4 else 0
class RT:
    def __init__(s,m,base=0x400000):
        s.m=m
        a,n,_=S.unpack('<III',m.read(base+0xd66254,12))
        s.names={}
        for i,p in enumerate(S.unpack('<%dI'%n,m.read(a,n*4))):
            if p: s.names[i]=m.read(p+16,256).split(b'\0')[0].decode('utf8','replace')
        a,n,_=S.unpack('<III',m.read(base+0xd7858c,12))
        s.objs={}
        for p in S.unpack('<%dI'%n,m.read(a,n*4)):
            if not p: continue
            d=m.read(p,0x3c)
            if len(d)<0x3c: continue
            s.objs[p]=dict(name=s.names.get(S.unpack_from('<I',d,0x2c)[0],'?'),outer=S.unpack_from('<I',d,0x28)[0],cls=S.unpack_from('<I',d,0x34)[0])
        for o in s.objs.values(): o['type']=s.objs.get(o['cls'],{}).get('name','?')
    def props(s,clsname):
        out=[]
        for p,o in s.objs.items():
            if o['type'].endswith('Property') and s.objs.get(o['outer'],{}).get('name')==clsname and s.objs[o['outer']]['type'] in ('Class','ScriptStruct'):
                out.append((s.m.u32(p+0x64),o['name'],o['type'],s.m.u32(p+0x44),s.m.u32(p+0x48),p))
        return sorted(out)
    def supers(s,cls):
        r=[]
        while cls and len(r)<32:
            r.append(s.objs.get(cls,{}).get('name','?')); cls=s.m.u32(cls+0x3c)
        return r
if __name__=='__main__':
    m=M(int(sys.argv[1])); rt=RT(m); cmd=sys.argv[2]
    if cmd=='classes':
        pat=sys.argv[3].lower()
        for p,o in rt.objs.items():
            if o['type']=='Class' and pat in o['name'].lower(): print(hex(p),o['name'],rt.supers(p))
    elif cmd=='props':
        for x in rt.props(sys.argv[3]): print(hex(x[0]),x[1],x[2],'dim',x[3],'size',x[4])
    elif cmd=='types':
        from collections import Counter
        c=Counter(o['type'] for o in rt.objs.values() if sys.argv[3].lower() in o['type'].lower())
        print(c.most_common(60))
    elif cmd=='inst':
        for p,o in rt.objs.items():
            if o['type']==sys.argv[3]: print(hex(p),o['name'],'outer',rt.objs.get(o['outer'],{}).get('name'))
def enum_names(rt,p):
    d=rt.m.read(p,0x60)
    for off in range(0x38,0x58,4):
        a,n,c=S.unpack_from('<III',d,off)
        if 0<n<=c<300 and a>0x10000:
            raw=rt.m.read(a,n*8)
            if len(raw)==n*8:
                vals=[rt.names.get(S.unpack_from('<I',raw,i*8)[0]) for i in range(n)]
                if all(vals): return off,vals
    return None
if __name__=='__main__' and sys.argv[2]=='enums':
    for p,o in rt.objs.items():
        if o['type']=='Enum' and sys.argv[3].lower() in o['name'].lower():
            print(o['name'], rt.objs.get(o['outer'],{}).get('name'), enum_names(rt,p))
def fstr(m,p):
    a,n,c=S.unpack('<III',m.read(p,12))
    if not a or n<=0 or n>512: return ''
    return m.read(a,n*2).decode('utf-16le','replace').rstrip('\0')
if __name__=='__main__' and sys.argv[2]=='drops':
    lv=[(p,o) for p,o in rt.objs.items() if o['type']=='DunDefDroppedEquipment' and rt.objs.get(o['outer'],{}).get('name')=='PersistentLevel']
    print(len(lv),'drops in levels')
    for p,o in lv[:int(sys.argv[3]) if len(sys.argv)>3 else 8]:
        eq=m.u32(p+0x324); loc=S.unpack('<3f',m.read(p+0xe8 if False else p,0) or b'\0'*12)
        e=m.read(eq,0x220)
        if len(e)<0x220: print('noeq'); continue
        stats=S.unpack_from('<11i',e,0x3c); dr=[S.unpack_from('<Ii',e,0x68+8*i) for i in range(4)]
        print(hex(p),o['name'],'eq',hex(eq),rt.objs.get(eq,{}).get('type'),'type',e[0xda],'lvl',S.unpack_from('<i',e,0x200)[0],'max',S.unpack_from('<i',e,0x124)[0],'rating',S.unpack_from('<ff',e,0x20c),
              'stats',stats,'dr',[(rt.objs.get(a,{}).get('name',hex(a)),b) for a,b in dr],'wdmg',S.unpack_from('<i',e,0x88)[0], 'name',fstr(m,eq+0x21c),'|',fstr(m,eq+0x128))
if __name__=='__main__' and sys.argv[2]=='statnames':
    eq=int(sys.argv[3],16)
    for off in (0x920,0x92c,0xa3c,0xa48,0xa54,0xa60):
        a,n,c=S.unpack('<III',m.read(eq+off,12))
        print(hex(off),n,[fstr(m,a+12*i) for i in range(min(n,40))])
if __name__=='__main__' and sys.argv[2]=='findprop':
    pat=sys.argv[3].lower()
    for p,o in rt.objs.items():
        if o['type'].endswith('Property') and pat in o['name'].lower():
            ow=rt.objs.get(o['outer'],{})
            if ow.get('type') in ('Class','ScriptStruct'): print(ow['name'],o['name'],o['type'],hex(m.u32(p+0x64)),'dim',m.u32(p+0x44))
if __name__=='__main__' and sys.argv[2]=='heronames':
    for p,o in rt.objs.items():
        if o['type']=='DunDefHeroManager' or (o['type'].startswith('DunDefHero') and o['name'].startswith('Default__') and o['type']=='DunDefHero'):
            off=0x160 if o['type']=='DunDefHeroManager' else 0x264
            print(o['name'],[fstr(m,p+off+12*i) for i in range(11)])
if __name__=='__main__' and sys.argv[2]=='rands':
    from collections import Counter
    nz=Counter(); tot=0
    for p,o in rt.objs.items():
        if o['type']=='HeroEquipment':
            d=m.read(p+0x228,440)
            if len(d)<440: continue
            tot+=1
            for i in range(11):
                if any(S.unpack_from('<10I',d,i*40)): nz[i]+=1
    print(tot,sorted(nz.items()))
