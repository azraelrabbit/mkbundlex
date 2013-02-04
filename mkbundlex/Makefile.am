all: build/mkbundlex.exe

build/mkbundlex.exe:
	mkdir -vp build
	xbuild /p:Configuration=Release mkbundlex.sln
	cp -vrf src/bin/Release/* build/

clean:
	rm -rvf build
	xbuild /p:Configuration=Release /t:Clean mkbundlex.sln

install: build/mkbundlex.exe
	mkdir -pv $(DESTDIR)$(PREFIX)/lib/mkbundlex/
	mkdir -pv $(DESTDIR)$(PREFIX)/bin/
	cp -vf build/* $(DESTDIR)$(PREFIX)/lib/mkbundlex/
	cp -vf nix_scripts/mkbundlex $(DESTDIR)$(PREFIX)/bin/

uninstall:
	rm -vrf $(DESTDIR)$(PREFIX)/lib/mkbundlex/
	rm -vrf $(DESTDIR)$(PREFIX)/bin/mkbundlex

