# Takes the Windows user name out of the test parts before they're shared (the repository holds corpus\generated).
#   python tools\scrub_corpus.py [folder ...]     (default: corpus)
# Inventor writes the user name into each file it saves: Author, Designer and Last Saved By, and the path it was saved
# to (C:\Users\<name>\...). Those sit in its property streams uncompressed, in ASCII or UTF-16, so the name is replaced
# in place by a word of the same length and no stream changes size. The JSON reports beside the parts (invre gen's truth
# files) get their paths made relative to the project, and the name replaced the same way. Running it twice changes
# nothing more. Check afterwards: case_test.py --decode should give the same results.
import os, re, sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def stand_in(n):
    return ("anonymous" * 4)[:n]


def scrub_binary(data, name):
    count = 0
    for encode in (lambda s: s.encode("ascii"), lambda s: s.encode("utf-16-le")):
        pattern = re.compile(re.escape(encode(name)), re.IGNORECASE)
        data, n = pattern.subn(encode(stand_in(len(name))), data)
        count += n
    return data, count


def scrub_text(text, name):
    # Paths into the project become relative: written plainly, or with JSON's doubled backslashes.
    text = text.replace(ROOT.replace("\\", "\\\\") + "\\\\", "").replace(ROOT + "\\", "")
    text, n = re.subn(re.escape(name), stand_in(len(name)), text, flags=re.IGNORECASE)
    return text, n


def main():
    name = os.environ.get("USERNAME") or ""
    if len(name) < 3:
        print("no user name to take out")
        return
    folders = sys.argv[1:] or [os.path.join(ROOT, "corpus")]
    changed = 0
    for folder in folders:
        for here, dirs, files in os.walk(folder):
            for f in files:
                path = os.path.join(here, f)
                ext = os.path.splitext(f)[1].lower()
                if ext in (".ipt", ".iam"):
                    with open(path, "rb") as fh:
                        old = fh.read()
                    new, n = scrub_binary(old, name)
                elif ext == ".json":
                    with open(path, encoding="utf-8") as fh:
                        old = fh.read()
                    new, n = scrub_text(old, name)
                    n = n or (new != old)
                else:
                    continue
                if new != old:
                    with open(path, "wb" if ext != ".json" else "w", **({} if ext != ".json" else {"encoding": "utf-8", "newline": ""})) as fh:
                        fh.write(new)
                    changed += 1
    print("files changed: %d" % changed)


if __name__ == "__main__":
    main()
