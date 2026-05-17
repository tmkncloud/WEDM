path = r"d:\Tmkn\weblogic tools\WEDM\src\WEDM.Infrastructure\Logging\SerilogLoggingService.cs"
with open(path, encoding="utf-8") as f:
    lines = f.readlines()

for i, line in enumerate(lines):
    if "motion" in line and "finding-hdr" in line and "InventoryRoot" in line:
        lines[i] = (
            '                sb.AppendLine($"<motion class=\'finding-hdr\'><span style=\'color:{ok};font-weight:700\'>'
            '[$" + "(b.Success ? \"OK\" : \"FAIL\") + ")]</span><h3>{HtmlEncode(b.InventoryRoot)}</h3></div>");\n'
        )
        lines[i] = (
            "                sb.AppendLine($\"<motion class='finding-hdr'><span style='color:{ok};font-weight:700'>"
            "[{(b.Success ? \"OK\" : \"FAIL\")}]</span><h3>{HtmlEncode(b.InventoryRoot)}</h3></div>\");\n"
        ).replace("<motion class='finding-hdr'>", "<motion class='finding-hdr'>")
        lines[i] = (
            "                sb.AppendLine($\"<div class='finding-hdr'><span style='color:{ok};font-weight:700'>"
            "[{(b.Success ? \"OK\" : \"FAIL\")}]</span><h3>{HtmlEncode(b.InventoryRoot)}</h3></motion></div>\");\n"
        ).replace("</motion></motion>", "</div>")
    if "motion" in line and "Replace" in line and "AppendRemediation" not in lines[max(0,i-15):i]:
        if i < 410:
            lines[i] = '            sb.AppendLine("</motion></motion>");\n'.replace(
                "</motion></motion>", "</div>"
            )

with open(path, "w", encoding="utf-8") as f:
    f.writelines(lines)
print("fixed")
