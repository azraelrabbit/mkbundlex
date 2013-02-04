all: build/mkbundlex.exe

build/mkbundlex.exe:
	mkdir -vp build
	xbuild /p:Configuration=Release mkbundlex.sln
	cp -vrf src/bin/Release/mkbundlex.exe build/
	cp -vrv nix_scripts/mkbundlex build/
	chmod +x build/mkbundlex
	

clean:
	rm -rvf build
	xbuild /p:Configuration=Release /t:Clean mkbundlex.sln

install: build/mkbundlex.exe
	mkdir -pv $(DESTDIR)$(PREFIX)/lib/mkbundlex/
	cp -vf build/mkbundlex.exe $(DESTDIR)$(PREFIX)/lib/mkbundlex/
	cp -vf build/mkbundlex $(DESTDIR)$(PREFIX)/bin/

uninstall:
	rm -vrf $(DESTDIR)$(PREFIX)/lib/mkbundlex/
	rm -vrv $(DESTDIR)$(PREFIX)/bin/mkbundlex

