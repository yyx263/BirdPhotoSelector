using BirdPhotoSelector;

/// <summary>Public-index regressions: search aliases must never rewrite displayed identities.</summary>
internal static class SpeciesSearchChecks
{
    public static int Run()
    {
        int count = 0;
        void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
            count++;
            Console.WriteLine("  OK SPECIES SEARCH " + message);
        }
        var middleEgret = new SpeciesIdentity("中白鹭", "Ardea intermedia", "taxon-middle");
        var greenTit = new SpeciesIdentity("綠背山雀", "Parus monticolus", "taxon-green");
        var latinOnly = new SpeciesIdentity("", "Phylloscopus borealis", "taxon-latin");
        var greatTit = new SpeciesIdentity("歐亞大山雀", "Parus major", "taxon-tit");
        var egret = new SpeciesIdentity("白鹭", "Egretta garzetta", "taxon-egret");
        var mandarin = new SpeciesIdentity("鴛鴦", "Aix galericulata", "taxon-mandarin");
        var littleGrebe = new SpeciesIdentity("小䴙䴘", "Tachybaptus ruficollis", "taxon-grebe");
        // The Chinese name is known to the fixed phonetic data; this Latin identity is deliberately not.
        var customEgret = new SpeciesIdentity("白鹭", "Customgenus customspecies", "custom-source-id");
        var paddedEgret = new SpeciesIdentity("　白鹭　", " Unknownus paddingbird ", " preserved-source-id ");
        var records = new[] { middleEgret, greenTit, latinOnly, greatTit, egret, mandarin, littleGrebe, customEgret, paddedEgret };
        var originalReferences = records.ToArray();
        var originalValues = records.Select(item => (item.ChineseName, item.ScientificName, item.SourceTaxonId, item.Key)).ToArray();
        var index = new SpeciesSearchIndex(records);
        var seen = new List<IReadOnlyList<SpeciesIdentity>>();

        IReadOnlyList<SpeciesIdentity> Search(string? query)
        {
            var matches = index.Search(query);
            seen.Add(matches);
            return matches;
        }
        bool SameOriginals(IReadOnlyList<SpeciesIdentity> matches) => matches.Count == records.Length &&
            matches.Select((item, position) => ReferenceEquals(item, records[position])).All(value => value);
        void Matches(SpeciesIdentity identity, string[] queries, string behavior)
        {
            foreach (string query in queries)
                Check(Search(query).Any(item => ReferenceEquals(item, identity)), behavior + ": " + query);
        }

        Check(new string?[] { null, "", " \t\r\n", "　 " }.All(query => SameOriginals(Search(query))),
            "null and whitespace queries preserve every original record in catalog order");
        Matches(greatTit, new[] { "欧亚大山雀", "歐亞大山雀", "歐亚大山雀", "　欧亞大山雀　" },
            "simplified, traditional and mixed Chinese queries find a traditional display name");
        Matches(greenTit, new[] { "绿背山雀", "綠背山雀" },
            "Chinese aliases fold both the record and the query without rewriting the record");
        Matches(egret, new[] { "白鹭", "白鷺" }, "egret name supports simplified and traditional forms");
        Matches(mandarin, new[] { "鸳鸯", "鴛鴦" }, "Mandarin duck name supports simplified and traditional forms");

        Matches(greatTit, new[]
        {
            "ouyadashanque", "oydsq", "OUYADASHANQUE", "O Y D S Q",
            "ou ya da shan que", "OU-YA-DA-SHAN-QUE", "ou'ya'da'shan'que",
            "ōu yà dà shān què", "ou1 ya4 da4 shan1 que4", "ｏｕｙａｄａｓｈａｎｑｕｅ", "ＯＹＤＳＱ"
        }, "great tit accepts full pinyin, initials and common input variations");
        Matches(egret, new[] { "bailu", "bl", "bái lù", "bai2-lu4" },
            "egret accepts plain, tonal and numbered pinyin");
        Matches(mandarin, new[] { "yuanyang", "yy", "yuān yāng", "yuan1yang1" },
            "Mandarin duck accepts full pinyin and initials");
        Matches(greenTit, new[]
        {
            "lvbeishanque", "lbsq", "lübeishanque", "lǜ bèi shān què",
            "lu:beishanque", "LV4-BEI4-SHAN1-QUE4", "ｌｖｂｅｉｓｈａｎｑｕｅ"
        }, "green tit treats umlaut, v and u-colon as the same phonetic input");
        Matches(littleGrebe, new[] { "xiaopiti", "xpt", "piti", "pi ti", "pt" },
            "rare grebe characters retain the verified pi-ti reading and substring search");
        Matches(littleGrebe, new[] { "小鸊鷉", "小鷿鷈", "小鸊䴘", "鸊鷉" },
            "rare traditional bird-character variants fold to the same original grebe identity");

        Matches(greatTit, new[] { " PARUS MAJOR ", "pArUs mAjOr", "major" },
            "scientific-name search retains trimming, case-insensitivity and substring semantics");
        Check(Search("PARUS").Any(item => ReferenceEquals(item, greatTit)) &&
              Search("PARUS").Any(item => ReferenceEquals(item, greenTit)),
            "a scientific genus query retains every matching species");
        Matches(latinOnly, new[] { "borealis", "PHYLLOSCOPUS" },
            "records without a Chinese name remain searchable by scientific name");
        Check(Search("bailu").Any(item => ReferenceEquals(item, customEgret)) &&
              Search("bl").Any(item => ReferenceEquals(item, customEgret)),
            "pinyin aliases are looked up by Chinese name even for a scientific identity absent from the fixed data");
        Matches(paddedEgret, new[] { "bailu", "bl" },
            "search-key trimming preserves a padded original record while finding its known Chinese pronunciation");

        foreach (string query in new[] { "白鹭", "白鷺" })
        {
            var matches = Search(query);
            int broad = Array.FindIndex(matches.ToArray(), item => ReferenceEquals(item, middleEgret));
            int exact = Array.FindIndex(matches.ToArray(), item => ReferenceEquals(item, egret));
            int customExact = Array.FindIndex(matches.ToArray(), item => ReferenceEquals(item, customEgret));
            int paddedExact = Array.FindIndex(matches.ToArray(), item => ReferenceEquals(item, paddedEgret));
            Check(exact >= 0 && customExact >= 0 && paddedExact >= 0 &&
                  broad > exact && broad > customExact && broad > paddedExact && matches[0].DisplayName == "白鹭",
                "exact Chinese matches rank before wider name/phonetic matches: " + query);
        }
        Check(Search("no-matching-species-zzzxq").Count == 0,
            "a nonmatching query does not manufacture a species or lose catalog entries");
        foreach (string query in new[] { "---", "'", "’", "12345", "@", "未收录的玄鸟名称", "\u0304" })
            Check(Search(query).Count == 0,
                "nonblank punctuation, standalone tones or unknown Chinese cannot become an empty-catalog query: " + query);
        Check(seen.SelectMany(matches => matches).All(item => originalReferences.Any(original => ReferenceEquals(item, original))),
            "search returns the original identity objects rather than replacing them with normalized aliases");
        Check(records.Select((item, position) => ReferenceEquals(item, originalReferences[position]) &&
                  (item.ChineseName, item.ScientificName, item.SourceTaxonId, item.Key) == originalValues[position]).All(value => value) &&
              SameOriginals(Search("")),
            "Chinese display spelling, scientific keys, source IDs and original record ordering remain unchanged after all searches");
        var empty = new SpeciesSearchIndex(Array.Empty<SpeciesIdentity>());
        Check(empty.Search("bailu").Count == 0 && empty.Search(null).Count == 0 && empty.Search("白鷺").Count == 0,
            "an empty index supports blank, Chinese and phonetic queries without depending on model resources");
        return count;
    }
}
