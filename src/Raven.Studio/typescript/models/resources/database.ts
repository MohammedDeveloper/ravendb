/// <reference path="../../../typings/tsd.d.ts"/>

import resource = require("models/resources/resource");
import license = require("models/auth/license");

class database extends resource {
    static readonly type = "database";
    static readonly qualifier = "db";

    constructor(dbInfo: Raven.Client.Server.Operations.DatabaseInfo) {
        super(dbInfo);

        this.updateUsing(dbInfo);
        /* TODO
        this.isLicensed = ko.pureComputed(() => {
            if (!!license.licenseStatus() && license.licenseStatus().IsCommercial) {
                var attributes = license.licenseStatus().Attributes;
                var result = this.activeBundles()
                    .map(bundleName => this.attributeValue(attributes, bundleName === "periodicBackup" ? "periodicExport" : bundleName))
                    .reduce((a, b) => /^true$/i.test(a) && /^true$/i.test(b), true);
                return result;
            }
            return true;
        });*/
        const dbName = dbInfo.Name;
        
    }

    private attributeValue(attributes: any, bundleName: string) {
        for (var key in attributes){
            if (attributes.hasOwnProperty(key) && key.toLowerCase() === bundleName.toLowerCase()) {
                return attributes[key];
            }
        }
        return "true";
    }

    static getNameFromUrl(url: string) {
        var index = url.indexOf("databases/");
        return (index > 0) ? url.substring(index + 10) : "";
    }

    isBundleActive(bundleName: string): boolean {
        if (bundleName) {
            return !!this.activeBundles().find((x: string) => x.toLowerCase() === bundleName.toLowerCase());
        }
        return false;
    }

    get fullTypeName() {
        return "Database";
    }

    get qualifier() {
        return database.qualifier;
    }

    get urlPrefix() {
        return "databases";
    }

    get type() {
        return database.type;
    }

    updateUsing(incomingCopy: Raven.Client.Server.Operations.DatabaseInfo): void {
        super.updateUsing(incomingCopy);

        //TODO: assign other props
    }
}

export = database;