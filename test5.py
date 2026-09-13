import urllib.request, json
# I'll just check the typical values for dwData.
# Asc("E") + 256 * Asc("C") => 17221 = 'C' 'E'? Wait 'C'=67, 'D'=68, 67 + 256*68 = 17475. (0x4344)
