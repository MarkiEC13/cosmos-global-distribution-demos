﻿function resolver(incomingRecord, existingRecord, isTombstone, conflictingRecords) {
    var collection = getContext().getCollection();

    if (!incomingRecord) {
        if (existingRecord) {

            collection.deleteDocument(existingRecord._self, {}, function (err, responseOptions) {
                if (err) throw err;
            });
        }
    } else if (isTombstone) {
        // delete always wins.
    } else {
        var documentToUse = incomingRecord;

        if (existingRecord) {
            if (documentToUse.userDefinedId > existingRecord.userDefinedId) {
                documentToUse = existingRecord;
            }
        }

        var i;
        for (i = 0; i < conflictingRecords.length; i++) {
            if (documentToUse.userDefinedId > conflictingRecords[i].userDefinedId) {
                documentToUse = conflictingRecords[i];
            }
        }

        tryDelete(conflictingRecords, incomingRecord, existingRecord, documentToUse);
    }

    function tryDelete(documents, incoming, existing, documentToInsert) {
        if (documents.length > 0) {
            collection.deleteDocument(documents[0]._self, {}, function (err, responseOptions) {
                if (err) throw err;

                documents.shift();
                tryDelete(documents, incoming, existing, documentToInsert);
            });
        } else if (existing) {
            collection.replaceDocument(existing._self, documentToInsert,
                function (err, documentCreated) {
                    if (err) throw err;
                });
        } else {
            collection.createDocument(collection.getSelfLink(), documentToInsert,
                function (err, documentCreated) {
                    if (err) throw err;
                });
        }
    }
}